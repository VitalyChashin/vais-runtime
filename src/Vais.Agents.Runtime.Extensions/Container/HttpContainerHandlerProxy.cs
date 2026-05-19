// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vais.Agents.Runtime.Extensions.Container.Wire;

namespace Vais.Agents.Runtime.Extensions.Container;

/// <summary>
/// Proxies a single seam handler call to a container extension via paired
/// <c>POST /handlers/&lt;id&gt;/pre</c> and <c>POST /handlers/&lt;id&gt;/post</c> HTTP calls.
/// </summary>
internal sealed class HttpContainerHandlerProxy
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _http;
    private readonly string _preEndpoint;
    private readonly string _postEndpoint;
    private readonly string _failureMode;
    private readonly ILogger _logger;

    internal HttpContainerHandlerProxy(
        HttpClient http,
        string preEndpoint,
        string postEndpoint,
        string failureMode,
        ILogger? logger = null)
    {
        _http = http;
        _preEndpoint = preEndpoint;
        _postEndpoint = postEndpoint;
        _failureMode = failureMode;
        _logger = logger ?? NullLogger.Instance;
    }

    internal async Task InvokeInputAsync(AgentInputContext ctx, Func<Task> next, CancellationToken ct)
    {
        var callId = Guid.NewGuid().ToString("N");
        var wire = new AgentInputContextWire(ctx.AgentId, ctx.RunId, ctx.NodeId, ctx.Message);
        var preReq = new AgentInputPreRequest(callId, wire);

        HandlerPreResponse? preResp;
        try
        {
            using var httpResp = await _http.PostAsJsonAsync(_preEndpoint, preReq, JsonOptions, ct).ConfigureAwait(false);
            httpResp.EnsureSuccessStatusCode();
            preResp = await httpResp.Content.ReadFromJsonAsync<HandlerPreResponse>(JsonOptions, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await HandleFailureModeAsync(next, ex).ConfigureAwait(false);
            return;
        }

        if (preResp is null)
        {
            _logger.LogWarning("pre-response null from {Endpoint}; applying failureMode={Mode}", _preEndpoint, _failureMode);
            await HandleFailureModeAsync(next, null).ConfigureAwait(false);
            return;
        }

        if (string.Equals(preResp.Action, "shortCircuit", StringComparison.OrdinalIgnoreCase))
            return;

        await next().ConfigureAwait(false);

        if (string.Equals(preResp.Action, "mutate", StringComparison.OrdinalIgnoreCase) && preResp.ContextPatch is { } patch)
            ApplyPatch(ctx.Properties, patch);

        var postReq = new AgentInputPostRequest(callId, preResp.ContinuationToken);
        try
        {
            using var postResp = await _http.PostAsJsonAsync(_postEndpoint, postReq, JsonOptions, ct).ConfigureAwait(false);
            postResp.EnsureSuccessStatusCode();
            var postBody = await postResp.Content.ReadFromJsonAsync<HandlerPostResponse>(JsonOptions, ct).ConfigureAwait(false);
            if (postBody is { Action: var action } && string.Equals(action, "mutate", StringComparison.OrdinalIgnoreCase) && postBody.ContextPatch is { } pp)
                ApplyPatch(ctx.Properties, pp);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "post call to {Endpoint} failed; swallowing.", _postEndpoint);
        }
    }

    internal async Task InvokeOutputAsync(AgentOutputContext ctx, Func<Task> next, CancellationToken ct)
    {
        var callId = Guid.NewGuid().ToString("N");
        var wire = new AgentOutputContextWire(
            ctx.AgentId, ctx.RunId, ctx.SessionId,
            ctx.Usage?.OutputTokens, ctx.Usage?.InputTokens);
        var preReq = new AgentOutputPreRequest(callId, wire);

        HandlerPreResponse? preResp;
        try
        {
            using var httpResp = await _http.PostAsJsonAsync(_preEndpoint, preReq, JsonOptions, ct).ConfigureAwait(false);
            httpResp.EnsureSuccessStatusCode();
            preResp = await httpResp.Content.ReadFromJsonAsync<HandlerPreResponse>(JsonOptions, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await HandleFailureModeAsync(next, ex).ConfigureAwait(false);
            return;
        }

        if (preResp is null)
        {
            _logger.LogWarning("pre-response null from {Endpoint}", _preEndpoint);
            return;
        }

        if (string.Equals(preResp.Action, "shortCircuit", StringComparison.OrdinalIgnoreCase))
            return;

        await next().ConfigureAwait(false);

        var postReq = new AgentOutputPostRequest(callId, preResp.ContinuationToken);
        try
        {
            using var postResp = await _http.PostAsJsonAsync(_postEndpoint, postReq, JsonOptions, ct).ConfigureAwait(false);
            postResp.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "post call to {Endpoint} failed; swallowing.", _postEndpoint);
        }
    }

    private async Task HandleFailureModeAsync(Func<Task> next, Exception? ex)
    {
        if (ex is not null)
            _logger.LogWarning(ex, "handler proxy {Endpoint} failed; failureMode={Mode}", _preEndpoint, _failureMode);

        if (string.Equals(_failureMode, "skip", StringComparison.OrdinalIgnoreCase))
        {
            await next().ConfigureAwait(false);
            return;
        }

        if (ex is not null)
            throw new ExtensionHandlerProxyException(_preEndpoint, ex);
    }

    private static void ApplyPatch(IDictionary<string, object?> target, IReadOnlyDictionary<string, object?> patch)
    {
        foreach (var kv in patch)
            target[kv.Key] = kv.Value;
    }
}

/// <summary>Thrown when a container handler proxy call fails and failureMode is 'fail'.</summary>
public sealed class ExtensionHandlerProxyException : Exception
{
    /// <summary>Construct a proxy exception for the given endpoint.</summary>
    public ExtensionHandlerProxyException(string endpoint, Exception inner)
        : base($"Container handler proxy call to '{endpoint}' failed.", inner) { }
}

/// <summary>
/// <see cref="AgentInputMiddleware"/> adapter that delegates to an <see cref="HttpContainerHandlerProxy"/>.
/// </summary>
internal sealed class AgentInputHandlerProxy : AgentInputMiddleware
{
    private readonly HttpContainerHandlerProxy _proxy;

    internal AgentInputHandlerProxy(HttpContainerHandlerProxy proxy) => _proxy = proxy;

    /// <inheritdoc />
    public override Task InvokeAsync(AgentInputContext ctx, Func<Task> next, CancellationToken ct = default)
        => _proxy.InvokeInputAsync(ctx, next, ct);
}

/// <summary>
/// <see cref="AgentOutputMiddleware"/> adapter that delegates to an <see cref="HttpContainerHandlerProxy"/>.
/// </summary>
internal sealed class AgentOutputHandlerProxy : AgentOutputMiddleware
{
    private readonly HttpContainerHandlerProxy _proxy;

    internal AgentOutputHandlerProxy(HttpContainerHandlerProxy proxy) => _proxy = proxy;

    /// <inheritdoc />
    public override Task InvokeAsync(AgentOutputContext ctx, Func<Task> next, CancellationToken ct = default)
        => _proxy.InvokeOutputAsync(ctx, next, ct);
}
