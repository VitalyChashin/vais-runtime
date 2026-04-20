// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Vais.Agents.Control.Http;

/// <summary>
/// Middleware that enforces <c>Idempotency-Key</c> semantics on write requests
/// to the control-plane HTTP surface. Mount via
/// <see cref="AgentControlPlaneIdempotencyApplicationBuilderExtensions.UseAgentControlPlaneIdempotency"/>
/// after <c>UseAuthentication</c> + <c>UseAgentControlPlanePrincipal</c> so the
/// tenant scope is populated.
/// </summary>
/// <remarks>
/// <para>
/// <b>Dispatch matrix</b> (from <see cref="IIdempotencyStore.TryBeginAsync"/>):
/// <list type="bullet">
///   <item><see cref="IdempotencyBeginStatus.New"/> — buffer the response; on 2xx/4xx call
///   <see cref="IIdempotencyStore.CompleteAsync"/>; on 5xx call <see cref="IIdempotencyStore.ReleaseAsync"/>.</item>
///   <item><see cref="IdempotencyBeginStatus.Replay"/> — write cached status + content-type + body; add
///   <c>Idempotency-Replayed: true</c> response header; do not call the handler.</item>
///   <item><see cref="IdempotencyBeginStatus.Mismatch"/> — 422 Problem Details <c>urn:vais-agents:idempotency-mismatch</c>.</item>
///   <item><see cref="IdempotencyBeginStatus.InFlight"/> — 409 Problem Details <c>urn:vais-agents:idempotency-in-flight</c> + <c>Retry-After: 1</c>.</item>
/// </list>
/// </para>
/// <para>
/// <b>Exclusions.</b> Requests are passed straight through (no dedupe) when any of:
/// <list type="bullet">
///   <item>No <c>Idempotency-Key</c> header present.</item>
///   <item>Method is <c>GET</c>/<c>HEAD</c>/<c>OPTIONS</c> (when <see cref="IdempotencyOptions.IncludeGetsInExclusion"/> is true).</item>
///   <item>Request path ends in <c>/healthz</c> or <c>/readyz</c>, or starts with any prefix in <see cref="IdempotencyOptions.PathExclusions"/>.</item>
///   <item>Outgoing response content-type is <c>text/event-stream</c> (future SSE endpoints opt out this way).</item>
/// </list>
/// </para>
/// </remarks>
public sealed class AgentControlPlaneIdempotencyMiddleware
{
    private const string HeaderName = "Idempotency-Key";
    private const string ReplayedHeaderName = "Idempotency-Replayed";

    private static readonly HashSet<string> _readOnlyMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        HttpMethods.Get, HttpMethods.Head, HttpMethods.Options,
    };

    private static readonly HashSet<string> _healthPathSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "/healthz", "/readyz",
    };

    private readonly RequestDelegate _next;
    private readonly IIdempotencyStore _store;
    private readonly IdempotencyOptions _options;
    private readonly ILogger<AgentControlPlaneIdempotencyMiddleware> _logger;

    /// <summary>Construct the middleware.</summary>
    public AgentControlPlaneIdempotencyMiddleware(
        RequestDelegate next,
        IIdempotencyStore store,
        IOptions<IdempotencyOptions> options,
        ILogger<AgentControlPlaneIdempotencyMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _next = next;
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>ASP.NET Core entry point.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Streaming endpoints decorated with [StreamingEndpoint] metadata bypass
        // the whole idempotency path — body-buffering is incompatible with SSE's
        // flush-as-you-go semantics; caching + replay is incompatible with a live
        // stream. v0.11's content-type-based opt-out covered CompleteAsync but
        // still buffered the body; the metadata check here opts out earlier.
        if (context.GetEndpoint()?.Metadata.GetMetadata<StreamingEndpointAttribute>() is not null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (ShouldSkip(context))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (!context.Request.Headers.TryGetValue(HeaderName, out var keyValues) || keyValues.Count == 0)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var keyValue = keyValues.ToString();
        if (string.IsNullOrWhiteSpace(keyValue))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }
        if (keyValue.Length > _options.MaxKeyLength)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new
            {
                error = $"Idempotency-Key header value exceeds {_options.MaxKeyLength} characters.",
            }).ConfigureAwait(false);
            return;
        }

        context.Request.EnableBuffering();
        string fingerprint = await ComputeFingerprintAsync(context.Request.Body, context.RequestAborted).ConfigureAwait(false);
        context.Request.Body.Position = 0;

        var idempotencyKey = BuildKey(context, keyValue);

        IdempotencyBeginResult beginResult;
        try
        {
            beginResult = await _store.TryBeginAsync(idempotencyKey, fingerprint, context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Idempotency store TryBeginAsync failed for key {Key}; falling back to pass-through.", keyValue);
            await _next(context).ConfigureAwait(false);
            return;
        }

        switch (beginResult.Status)
        {
            case IdempotencyBeginStatus.Replay:
                await WriteReplayAsync(context, beginResult.CachedResponse!).ConfigureAwait(false);
                return;

            case IdempotencyBeginStatus.Mismatch:
                await WriteProblemAsync(
                    context,
                    ProblemDetailsMapping.IdempotencyMismatch(keyValue, beginResult.ExistingFingerprint, context.Request.Path))
                    .ConfigureAwait(false);
                return;

            case IdempotencyBeginStatus.InFlight:
                await WriteProblemAsync(
                    context,
                    ProblemDetailsMapping.IdempotencyInFlight(keyValue, instance: context.Request.Path))
                    .ConfigureAwait(false);
                return;

            case IdempotencyBeginStatus.New:
            default:
                await HandleNewAsync(context, idempotencyKey).ConfigureAwait(false);
                return;
        }
    }

    private async Task HandleNewAsync(HttpContext context, IdempotencyKey key)
    {
        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        var released = false;
        try
        {
            try
            {
                await _next(context).ConfigureAwait(false);
            }
            catch
            {
                // Handler threw — release and rethrow; the downstream error handler / exception
                // page (or kestrel default) will emit its own response on the original stream.
                context.Response.Body = originalBody;
                released = true;
                await _store.ReleaseAsync(key, context.RequestAborted).ConfigureAwait(false);
                throw;
            }

            buffer.Position = 0;
            var bodyBytes = buffer.ToArray();
            var contentType = context.Response.ContentType ?? "application/octet-stream";
            var status = context.Response.StatusCode;

            var isStreaming = contentType.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase);

            if (!isStreaming && status >= 200 && status < 500)
            {
                var cached = new CachedResponse(
                    status,
                    contentType,
                    bodyBytes,
                    DateTimeOffset.UtcNow);
                try
                {
                    await _store.CompleteAsync(key, cached, context.RequestAborted).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Idempotency store CompleteAsync failed for key {Key}; response delivery continues.", key.Key);
                }
            }
            else
            {
                try
                {
                    await _store.ReleaseAsync(key, context.RequestAborted).ConfigureAwait(false);
                    released = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Idempotency store ReleaseAsync failed for key {Key}.", key.Key);
                }
            }

            context.Response.Headers[ReplayedHeaderName] = "false";
            context.Response.Body = originalBody;
            await originalBody.WriteAsync(bodyBytes, context.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            if (ReferenceEquals(context.Response.Body, buffer))
            {
                context.Response.Body = originalBody;
            }
            if (!released)
            {
                // Nothing to do — entry either completed or was already released.
            }
        }
    }

    private static async Task WriteReplayAsync(HttpContext context, CachedResponse cached)
    {
        context.Response.StatusCode = cached.StatusCode;
        context.Response.ContentType = cached.ContentType;
        context.Response.Headers[ReplayedHeaderName] = "true";
        if (cached.Body.Length > 0)
        {
            await context.Response.Body.WriteAsync(cached.Body, context.RequestAborted).ConfigureAwait(false);
        }
    }

    private static async Task WriteProblemAsync(HttpContext context, IResult result)
    {
        await result.ExecuteAsync(context).ConfigureAwait(false);
    }

    private bool ShouldSkip(HttpContext context)
    {
        var method = context.Request.Method;
        if (_options.IncludeGetsInExclusion && _readOnlyMethods.Contains(method))
        {
            return true;
        }
        var path = context.Request.Path.ToString();
        foreach (var suffix in _healthPathSuffixes)
        {
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        foreach (var exclusion in _options.PathExclusions)
        {
            if (path.StartsWith(exclusion, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static IdempotencyKey BuildKey(HttpContext context, string keyValue)
    {
        var tenantId = context.User?.FindFirst("tenant_id")?.Value
            ?? context.User?.FindFirst("tid")?.Value;
        return new IdempotencyKey(
            TenantId: tenantId,
            Method: context.Request.Method,
            Path: context.Request.Path.ToString(),
            Key: keyValue);
    }

    private static async Task<string> ComputeFingerprintAsync(Stream body, CancellationToken cancellationToken)
    {
        using var sha = SHA256.Create();
        if (!body.CanSeek)
        {
            // Defensive — EnableBuffering should have made the stream seekable.
            using var copy = new MemoryStream();
            await body.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
            copy.Position = 0;
            var hash = sha.ComputeHash(copy);
            return Convert.ToHexString(hash);
        }
        body.Position = 0;
        var hashBytes = await sha.ComputeHashAsync(body, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hashBytes);
    }
}
