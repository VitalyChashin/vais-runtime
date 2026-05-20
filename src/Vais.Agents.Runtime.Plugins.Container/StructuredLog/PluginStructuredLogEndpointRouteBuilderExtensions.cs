// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vais.Agents.Core;

namespace Vais.Agents.Runtime.Plugins.Container.StructuredLog;

/// <summary>
/// Maps the structured-log receiver endpoint on the internal port.
/// Container plugins and extensions post log records to <c>POST /v1/logs</c> with
/// <c>Authorization: vais-plugin-token &lt;token&gt;</c>.
/// Optional query parameters discriminate the source:
/// <c>?source=plugin|extension&amp;id=&lt;id&gt;</c>.
/// The runtime validates the token and fans the record out to the runtime's
/// <see cref="ILogger"/> pipeline (docker-logs, ELK/Loki, etc.).
/// </summary>
public static class PluginStructuredLogEndpointRouteBuilderExtensions
{
    private const string AuthScheme = "vais-plugin-token";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Adds <c>POST /v1/logs</c> to the pipeline.
    /// Requires <see cref="ICallTokenService"/> to be registered.
    /// No-ops gracefully when <see cref="ICallTokenService"/> is absent.
    /// </summary>
    public static IEndpointRouteBuilder MapPluginStructuredLogEndpoints(
        this IEndpointRouteBuilder builder)
    {
        var callTokenService = builder.ServiceProvider.GetService<ICallTokenService>();
        if (callTokenService is null)
            return builder;

        var loggerFactory = builder.ServiceProvider.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger(typeof(PluginStructuredLogEndpointRouteBuilderExtensions).FullName!);
        var forwarder = new StructuredLogForwarder(logger);

        builder.MapPost("/v1/logs", async (HttpContext ctx) =>
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

            if (!ctx.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) ?? true)
            {
                ctx.Response.StatusCode = 415;
                return;
            }

            PluginLogRecord? record;
            try
            {
                record = await JsonSerializer.DeserializeAsync<PluginLogRecord>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Failed to parse structured-log JSON body from agent '{AgentId}'", agentId);
                ctx.Response.StatusCode = 400;
                return;
            }

            if (record is null)
            {
                ctx.Response.StatusCode = 400;
                return;
            }

            // Optional URL discriminator: ?source=extension&id=<extension-id>
            var source = ctx.Request.Query.TryGetValue("source", out var srcVal)
                ? srcVal.ToString() : "plugin";
            var extensionId = ctx.Request.Query.TryGetValue("id", out var idVal)
                ? idVal.ToString() : (string?)null;

            try
            {
                forwarder.Forward(record, agentId, source, extensionId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error forwarding structured log from agent '{AgentId}'", agentId);
            }

            ctx.Response.StatusCode = 200;
        });

        return builder;
    }
}
