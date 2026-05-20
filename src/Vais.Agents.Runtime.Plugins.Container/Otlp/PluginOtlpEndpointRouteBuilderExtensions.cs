// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vais.Agents.Core;

namespace Vais.Agents.Runtime.Plugins.Container.Otlp;

/// <summary>
/// Maps the OTLP trace receiver endpoint on the internal port (5001).
/// Container plugins and extensions send spans to <c>POST /v1/otlp/v1/traces</c> with
/// <c>Authorization: vais-plugin-token &lt;token&gt;</c>.
/// Optional query parameters discriminate the source:
/// <c>?source=extension&amp;id=&lt;extension-id&gt;</c> causes forwarded spans to be tagged
/// <c>vais.span.source=extension_otlp</c> and <c>vais.extension.id=&lt;id&gt;</c>.
/// The runtime validates the token, extracts the agent identity, and re-emits
/// the spans as .NET <see cref="System.Diagnostics.Activity"/> objects so they
/// flow through the existing OpenTelemetry pipeline.
/// </summary>
public static class PluginOtlpEndpointRouteBuilderExtensions
{
    private const string ContentTypeProtobuf = "application/x-protobuf";
    private const string AuthScheme = "vais-plugin-token";

    /// <summary>
    /// Adds <c>POST /v1/otlp/v1/traces</c> to the internal-port pipeline.
    /// Requires <see cref="ICallTokenService"/> to be registered in the service container.
    /// No-ops gracefully when <see cref="ICallTokenService"/> is absent.
    /// </summary>
    public static IEndpointRouteBuilder MapPluginOtlpEndpoints(
        this IEndpointRouteBuilder builder)
    {
        var callTokenService = builder.ServiceProvider.GetService<ICallTokenService>();
        if (callTokenService is null)
            return builder;

        var loggerFactory = builder.ServiceProvider.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger(typeof(PluginOtlpEndpointRouteBuilderExtensions).FullName!);
        var forwarder = new OtlpSpanForwarder(logger);

        builder.MapPost("/v1/otlp/v1/traces", async (HttpContext ctx) =>
        {
            // Validate Authorization header: vais-plugin-token <token>
            var authHeader = ctx.Request.Headers.Authorization.FirstOrDefault() ?? string.Empty;
            string? token = null;
            if (authHeader.StartsWith(AuthScheme + " ", StringComparison.OrdinalIgnoreCase))
                token = authHeader[(AuthScheme.Length + 1)..].Trim();

            if (string.IsNullOrEmpty(token) || !callTokenService.TryExtract(token, out _, out var agentId))
            {
                ctx.Response.StatusCode = 401;
                return;
            }

            if (!ctx.Request.ContentType?.StartsWith(ContentTypeProtobuf, StringComparison.OrdinalIgnoreCase) ?? true)
            {
                ctx.Response.StatusCode = 415;
                return;
            }

            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted).ConfigureAwait(false);
            var body = ms.ToArray();

            List<OtlpSpan> spans;
            try
            {
                spans = OtlpTraceParser.ParseExportRequest(body);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to parse OTLP protobuf body from plugin '{AgentId}'", agentId);
                ctx.Response.StatusCode = 400;
                return;
            }

            // Optional URL discriminator: ?source=extension&id=<extension-id>
            var source = ctx.Request.Query.TryGetValue("source", out var srcVal)
                ? srcVal.ToString() : "plugin_otlp";
            var extensionId = ctx.Request.Query.TryGetValue("id", out var idVal)
                ? idVal.ToString() : (string?)null;

            try
            {
                forwarder.Forward(spans, agentId, source, extensionId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error forwarding OTLP spans from plugin '{AgentId}'", agentId);
            }

            ctx.Response.StatusCode = 200;
        });

        return builder;
    }
}
