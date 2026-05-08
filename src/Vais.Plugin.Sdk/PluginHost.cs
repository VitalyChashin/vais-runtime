// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Vais.Plugin.Sdk;

/// <summary>
/// Entry point for building a container plugin host.
/// </summary>
/// <example>
/// Typical <c>Program.cs</c>:
/// <code>
/// var app = PluginHost.CreateBuilder(args)
///     .AddPlugin&lt;MyAgent&gt;(targetApiVersion: "0.24")
///     .Build();
/// await app.RunAsync();
/// </code>
/// </example>
public static class PluginHost
{
    /// <summary>Creates a <see cref="PluginHostBuilder"/> using <c>WebApplication.CreateBuilder</c>.</summary>
    public static PluginHostBuilder CreateBuilder(string[] args) =>
        new(WebApplication.CreateBuilder(args));
}

/// <summary>Fluent builder for a container plugin host.</summary>
public sealed class PluginHostBuilder(WebApplicationBuilder inner)
{
    /// <summary>Exposes the underlying <see cref="IServiceCollection"/> for custom DI registrations.</summary>
    public IServiceCollection Services => inner.Services;

    /// <summary>Exposes the underlying <see cref="WebApplicationBuilder"/> for advanced host configuration.</summary>
    public WebApplicationBuilder ApplicationBuilder => inner;

    /// <summary>
    /// Registers <typeparamref name="TAgent"/> as the plugin handler and records metadata for
    /// <c>GET /v1/metadata</c>.
    /// </summary>
    /// <param name="targetApiVersion">API version string declared in the plugin manifest (e.g. <c>"0.24"</c>).</param>
    /// <param name="handlerTypeName">
    /// Fully-qualified handler type name. Defaults to <c>typeof(TAgent).FullName</c>.
    /// Must match the <c>vais.plugin.handlerTypeName</c> OCI image label.
    /// </param>
    public PluginHostBuilder AddPlugin<TAgent>(
        string targetApiVersion,
        string? handlerTypeName = null)
        where TAgent : ContainerPluginAgent, new()
    {
        inner.Services.AddSingleton<ContainerPluginAgent, TAgent>();
        inner.Services.AddSingleton(new PluginMetadata(
            HandlerTypeName: handlerTypeName ?? (typeof(TAgent).FullName ?? typeof(TAgent).Name),
            TargetApiVersion: targetApiVersion));
        return this;
    }

    /// <summary>Builds the <see cref="WebApplication"/> with all plugin endpoints registered.</summary>
    public WebApplication Build()
    {
        var app = inner.Build();
        app.MapPluginEndpoints();
        return app;
    }
}

internal sealed record PluginMetadata(string HandlerTypeName, string TargetApiVersion);

internal static class PluginEndpointsExtensions
{
    private static readonly string SdkVersion =
        typeof(PluginHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    internal static void MapPluginEndpoints(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ready" }));

        app.MapGet("/v1/metadata", (PluginMetadata meta) =>
            Results.Json(new
            {
                handlerTypeName = meta.HandlerTypeName,
                targetApiVersion = meta.TargetApiVersion,
                capabilities = new[] { "invoke", "stream" },
                sdkVersion = SdkVersion,
            }, PluginJsonOptions.Default));

        app.MapPost("/v1/invoke", async (HttpContext ctx, ContainerPluginAgent agent, IServiceProvider sp) =>
        {
            var request = await ctx.Request.ReadFromJsonAsync<InvokeRequest>(PluginJsonOptions.Default, ctx.RequestAborted)
                .ConfigureAwait(false);
            if (request is null)
                return Results.BadRequest(new { errorType = "InternalError", errorMessage = "Empty or invalid request body." });

            request.Llm = sp.GetService<ILlmGatewayClient>()
                ?? new DefaultLlmGatewayClient(CreateHttpClient(request.LlmGatewayUrl), request.Context, request.AgentId);
            request.Tools = sp.GetService<IToolGatewayClient>()
                ?? new DefaultToolGatewayClient(CreateHttpClient(request.ToolGatewayUrl), request.Context, request.AgentId);

            try
            {
                var response = await agent.InvokeAsync(request, ctx.RequestAborted).ConfigureAwait(false);
                return Results.Json(response, PluginJsonOptions.Default);
            }
            catch (OpaqueStateDeserializationException ex)
            {
                return Results.Json(
                    new { errorType = "OpaqueStateDeserializationError", errorMessage = ex.Message, diagnosticTail = (string?)null },
                    statusCode: 422);
            }
            catch (Exception ex)
            {
                var diag = ex.ToString();
                if (diag.Length > 500) diag = diag[..500];
                return Results.Json(
                    new { errorType = "InternalError", errorMessage = ex.Message, diagnosticTail = diag },
                    statusCode: 500);
            }
        });

        app.MapPost("/v1/stream", async (HttpContext ctx, ContainerPluginAgent agent, IServiceProvider sp) =>
        {
            var request = await ctx.Request.ReadFromJsonAsync<InvokeRequest>(PluginJsonOptions.Default, ctx.RequestAborted)
                .ConfigureAwait(false);
            if (request is null)
            {
                ctx.Response.StatusCode = 400;
                return;
            }

            request.Llm = sp.GetService<ILlmGatewayClient>()
                ?? new DefaultLlmGatewayClient(CreateHttpClient(request.LlmGatewayUrl), request.Context, request.AgentId);
            request.Tools = sp.GetService<IToolGatewayClient>()
                ?? new DefaultToolGatewayClient(CreateHttpClient(request.ToolGatewayUrl), request.Context, request.AgentId);

            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";

            var enumerator = agent.StreamAsync(request, ctx.RequestAborted).GetAsyncEnumerator(ctx.RequestAborted);
            await using var _ = enumerator;

            try
            {
                while (true)
                {
                    var moveNext = enumerator.MoveNextAsync();

                    // Fast path: already completed synchronously.
                    if (moveNext.IsCompleted)
                    {
                        if (!moveNext.Result) break;
                        await WriteEvent(ctx, enumerator.Current).ConfigureAwait(false);
                        continue;
                    }

                    // Slow path: race against heartbeat interval.
                    while (!moveNext.IsCompleted)
                    {
                        var heartbeatDelay = Task.Delay(TimeSpan.FromSeconds(15), ctx.RequestAborted);
                        await Task.WhenAny(moveNext.AsTask(), heartbeatDelay).ConfigureAwait(false);
                        if (!moveNext.IsCompleted)
                        {
                            await ctx.Response.WriteAsync(SseWriter.HeartbeatComment, ctx.RequestAborted).ConfigureAwait(false);
                            await ctx.Response.Body.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
                        }
                    }

                    if (!await moveNext) break;
                    await WriteEvent(ctx, enumerator.Current).ConfigureAwait(false);
                }
            }
            catch (OpaqueStateDeserializationException ex)
            {
                await ctx.Response.WriteAsync(SseWriter.EncodeError("OpaqueStateDeserializationError", ex.Message), ctx.RequestAborted).ConfigureAwait(false);
                await ctx.Response.WriteAsync(SseWriter.EncodeEvent(new SseEvent("done", new InvokeResponse())), ctx.RequestAborted).ConfigureAwait(false);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var diag = ex.ToString();
                if (diag.Length > 500) diag = diag[..500];
                await ctx.Response.WriteAsync(SseWriter.EncodeError("InternalError", ex.Message), ctx.RequestAborted).ConfigureAwait(false);
                await ctx.Response.WriteAsync(SseWriter.EncodeEvent(new SseEvent("done", new InvokeResponse())), ctx.RequestAborted).ConfigureAwait(false);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
            }
        });
    }

    private static async Task WriteEvent(HttpContext ctx, SseEvent evt)
    {
        await ctx.Response.WriteAsync(SseWriter.EncodeEvent(evt), ctx.RequestAborted).ConfigureAwait(false);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
    }

    private static HttpClient CreateHttpClient(string baseUrl)
    {
        var url = baseUrl.TrimEnd('/') + '/';
        return new HttpClient { BaseAddress = new Uri(url) };
    }
}
