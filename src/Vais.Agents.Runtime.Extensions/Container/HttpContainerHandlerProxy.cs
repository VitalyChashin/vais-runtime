// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
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
/// Each invocation is wrapped in a <c>vais.extension.handler.invoke</c> OTel span and
/// its duration/action recorded on the <c>vais_extension_handler_invoke_*</c> instruments.
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
    private readonly HandlerBindingDescriptor _descriptor;
    private readonly ILogger _logger;

    internal HttpContainerHandlerProxy(
        HttpClient http,
        string preEndpoint,
        string postEndpoint,
        string failureMode,
        HandlerBindingDescriptor descriptor,
        ILogger? logger = null)
    {
        _http = http;
        _preEndpoint = preEndpoint;
        _postEndpoint = postEndpoint;
        _failureMode = failureMode;
        _descriptor = descriptor;
        _logger = logger ?? NullLogger.Instance;
    }

    internal Task InvokeInputAsync(AgentInputContext ctx, Func<Task> next, CancellationToken ct)
        => ExtensionInvocationInstrumentation.InvokeWithInstrumentationAsync(
            _descriptor, ctx.AgentId, ctx.RunId, ctx.NodeId,
            () => InvokeInputCoreAsync(ctx, next, ct),
            ct);

    internal Task InvokeOutputAsync(AgentOutputContext ctx, Func<Task> next, CancellationToken ct)
        => ExtensionInvocationInstrumentation.InvokeWithInstrumentationAsync(
            _descriptor, ctx.AgentId, ctx.RunId, nodeId: null,
            () => InvokeOutputCoreAsync(ctx, next, ct),
            ct);

    internal async Task<ToolCallOutcome> InvokeToolAsync(
        ToolGatewayContext ctx, Func<Task<ToolCallOutcome>> next, CancellationToken ct)
    {
        var actx = ctx.AgentContext;
        ToolCallOutcome? captured = null;
        await ExtensionInvocationInstrumentation.InvokeWithInstrumentationAsync(
            _descriptor, actx.AgentName ?? "", actx.RunId, nodeId: null,
            async () =>
            {
                var (outcome, handler) = await InvokeToolCoreAsync(ctx, next, ct).ConfigureAwait(false);
                captured = outcome;
                return handler;
            },
            ct).ConfigureAwait(false);
        return captured!;
    }

    private async Task<HandlerOutcome> InvokeInputCoreAsync(AgentInputContext ctx, Func<Task> next, CancellationToken ct)
    {
        var callId = Guid.NewGuid().ToString("N");
        var wire = new AgentInputContextWire(ctx.AgentId, ctx.RunId, ctx.NodeId, ctx.Message);
        var preReq = new AgentInputPreRequest(callId, wire);

        HandlerPreResponse? preResp;
        try
        {
            preResp = await SendPreAsync<AgentInputPreRequest, HandlerPreResponse>(
                preReq, ctx.AgentId, ctx.RunId, ctx.NodeId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return await HandleFailureModeAsync(next, ctx.AgentId, ctx.RunId, ex).ConfigureAwait(false);
        }

        if (preResp is null)
        {
            _logger.LogWarning(
                "pre-response null from {Endpoint}; agentId={AgentId} runId={RunId} failureMode={Mode}",
                _preEndpoint, ctx.AgentId, ctx.RunId, _failureMode);
            return await HandleFailureModeAsync(next, ctx.AgentId, ctx.RunId, null).ConfigureAwait(false);
        }

        if (string.Equals(preResp.Action, "shortCircuit", StringComparison.OrdinalIgnoreCase))
            return HandlerOutcome.ShortCircuit();

        await next().ConfigureAwait(false);

        var actionLabel = "next";
        if (string.Equals(preResp.Action, "mutate", StringComparison.OrdinalIgnoreCase) && preResp.ContextPatch is { } patch)
        {
            ApplyPatch(ctx.Properties, patch);
            actionLabel = "mutate";
        }

        var postReq = new AgentInputPostRequest(callId, preResp.ContinuationToken);
        var postBody = await TrySendPostAsync<AgentInputPostRequest, HandlerPostResponse>(
            postReq, ctx.AgentId, ctx.RunId, ctx.NodeId, ct).ConfigureAwait(false);
        if (postBody is { Action: var action } && string.Equals(action, "mutate", StringComparison.OrdinalIgnoreCase) && postBody.ContextPatch is { } pp)
        {
            ApplyPatch(ctx.Properties, pp);
            actionLabel = "mutate";
        }

        return actionLabel == "mutate" ? HandlerOutcome.Mutate() : HandlerOutcome.Next();
    }

    private async Task<HandlerOutcome> InvokeOutputCoreAsync(AgentOutputContext ctx, Func<Task> next, CancellationToken ct)
    {
        var callId = Guid.NewGuid().ToString("N");
        var wire = new AgentOutputContextWire(
            ctx.AgentId, ctx.RunId, ctx.SessionId,
            ctx.Usage?.OutputTokens, ctx.Usage?.InputTokens);
        var preReq = new AgentOutputPreRequest(callId, wire);

        HandlerPreResponse? preResp;
        try
        {
            preResp = await SendPreAsync<AgentOutputPreRequest, HandlerPreResponse>(
                preReq, ctx.AgentId, ctx.RunId, nodeId: null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return await HandleFailureModeAsync(next, ctx.AgentId, ctx.RunId, ex).ConfigureAwait(false);
        }

        if (preResp is null)
        {
            _logger.LogWarning("pre-response null from {Endpoint}; agentId={AgentId} runId={RunId}",
                _preEndpoint, ctx.AgentId, ctx.RunId);
            return HandlerOutcome.ShortCircuit();
        }

        if (string.Equals(preResp.Action, "shortCircuit", StringComparison.OrdinalIgnoreCase))
            return HandlerOutcome.ShortCircuit();

        await next().ConfigureAwait(false);

        var postReq = new AgentOutputPostRequest(callId, preResp.ContinuationToken);
        await TrySendPostAsync<AgentOutputPostRequest, HandlerPostResponse>(
            postReq, ctx.AgentId, ctx.RunId, nodeId: null, ct).ConfigureAwait(false);

        return HandlerOutcome.Next();
    }

    private async Task<(ToolCallOutcome Outcome, HandlerOutcome Handler)> InvokeToolCoreAsync(
        ToolGatewayContext ctx, Func<Task<ToolCallOutcome>> next, CancellationToken ct)
    {
        var actx = ctx.AgentContext;
        var callId = ctx.CallId;
        var wire = new ToolGatewayContextWire(
            ctx.ToolName, ctx.CallId, ctx.Arguments,
            actx.AgentName ?? "", actx.RunId,
            actx.PrivilegeLevel?.ToString(), actx.WorkspaceId,
            actx.AllowedTools?.ToArray());
        var preReq = new ToolGatewayPreRequest(callId, wire);

        ToolGatewayPreResponse? preResp;
        try
        {
            preResp = await SendPreAsync<ToolGatewayPreRequest, ToolGatewayPreResponse>(
                preReq, actx.AgentName ?? "", actx.RunId, nodeId: null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return await HandleToolFailureModeAsync(ctx, next, ex).ConfigureAwait(false);
        }

        if (preResp is null)
        {
            _logger.LogWarning(
                "tool pre-response null from {Endpoint}; agentId={AgentId} runId={RunId} failureMode={Mode}",
                _preEndpoint, actx.AgentName, actx.RunId, _failureMode);
            return await HandleToolFailureModeAsync(ctx, next, null).ConfigureAwait(false);
        }

        if (string.Equals(preResp.Action, "shortCircuit", StringComparison.OrdinalIgnoreCase))
        {
            var denied = new ToolCallOutcome(ctx.CallId, preResp.Result, preResp.Error);
            return (denied, HandlerOutcome.ShortCircuit());
        }

        var outcome = await next().ConfigureAwait(false);

        var postReq = new ToolGatewayPostRequest(callId, preResp.ContinuationToken, outcome.Result, outcome.Error);
        var postResp = await TrySendPostAsync<ToolGatewayPostRequest, ToolGatewayPostResponse>(
            postReq, actx.AgentName ?? "", actx.RunId, nodeId: null, ct).ConfigureAwait(false);

        if (postResp is not null && string.Equals(postResp.Action, "mutate", StringComparison.OrdinalIgnoreCase))
        {
            return (new ToolCallOutcome(ctx.CallId, postResp.Result, postResp.Error), HandlerOutcome.Mutate());
        }

        return (outcome, HandlerOutcome.Next());
    }

    /// <summary>
    /// Applies the handler's failure-mode for the tool seam. <c>skip</c> dispatches the tool as if the
    /// handler were absent; <c>fail</c> rethrows on a transport error and fails closed (deny) when the
    /// handler returns no pre-response — a governance handler that cannot answer must not silently allow.
    /// </summary>
    private async Task<(ToolCallOutcome Outcome, HandlerOutcome Handler)> HandleToolFailureModeAsync(
        ToolGatewayContext ctx, Func<Task<ToolCallOutcome>> next, Exception? ex)
    {
        var actx = ctx.AgentContext;
        if (ex is not null)
            _logger.LogWarning(ex,
                "tool handler proxy {Endpoint} failed; agentId={AgentId} runId={RunId} failureMode={Mode}",
                _preEndpoint, actx.AgentName, actx.RunId, _failureMode);

        if (string.Equals(_failureMode, "skip", StringComparison.OrdinalIgnoreCase))
        {
            var outcome = await next().ConfigureAwait(false);
            return (outcome, ex is not null ? HandlerOutcome.Skip(ex) : HandlerOutcome.Next());
        }

        if (ex is not null)
            throw new ExtensionHandlerProxyException(_preEndpoint, ex);

        var denied = new ToolCallOutcome(
            ctx.CallId, Result: null,
            Error: "ExtensionToolGatewayError: handler returned no pre-response (failureMode=fail).");
        return (denied, HandlerOutcome.ShortCircuit());
    }

    // ── Shared /pre + /post envelope ──────────────────────────────────────────

    /// <summary>
    /// Sends the <c>/pre</c> request and parses the <see cref="HandlerPreResponse"/>. Transport and
    /// non-success HTTP errors propagate to the caller, which applies the seam's failure-mode policy.
    /// </summary>
    private async Task<TResp?> SendPreAsync<TReq, TResp>(
        TReq preReq, string agentId, string? runId, string? nodeId, CancellationToken ct)
        where TResp : class
    {
        using var preMsg = CreateTracedRequest(_preEndpoint, preReq, agentId, runId, nodeId);
        using var httpResp = await _http.SendAsync(preMsg, ct).ConfigureAwait(false);
        httpResp.EnsureSuccessStatusCode();
        return await httpResp.Content.ReadFromJsonAsync<TResp>(JsonOptions, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the best-effort <c>/post</c> request. The post phase never aborts the chain: any error is
    /// logged at WARN and swallowed (returns null), matching the shipped input/output semantics.
    /// </summary>
    private async Task<TResp?> TrySendPostAsync<TReq, TResp>(
        TReq postReq, string agentId, string? runId, string? nodeId, CancellationToken ct)
        where TResp : class
    {
        try
        {
            using var postMsg = CreateTracedRequest(_postEndpoint, postReq, agentId, runId, nodeId);
            using var postResp = await _http.SendAsync(postMsg, ct).ConfigureAwait(false);
            postResp.EnsureSuccessStatusCode();
            return await postResp.Content.ReadFromJsonAsync<TResp>(JsonOptions, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "post call to {Endpoint} failed; agentId={AgentId} runId={RunId}; swallowing.",
                _postEndpoint, agentId, runId);
            return null;
        }
    }

    private async Task<HandlerOutcome> HandleFailureModeAsync(
        Func<Task> next, string agentId, string? runId, Exception? ex)
    {
        if (ex is not null)
            _logger.LogWarning(ex,
                "handler proxy {Endpoint} failed; agentId={AgentId} runId={RunId} failureMode={Mode}",
                _preEndpoint, agentId, runId, _failureMode);

        if (string.Equals(_failureMode, "skip", StringComparison.OrdinalIgnoreCase))
        {
            await next().ConfigureAwait(false);
            return ex is not null ? HandlerOutcome.Skip(ex) : HandlerOutcome.Next();
        }

        if (ex is not null)
            throw new ExtensionHandlerProxyException(_preEndpoint, ex);

        return HandlerOutcome.ShortCircuit();
    }

    private static HttpRequestMessage CreateTracedRequest<T>(
        string endpoint, T body, string agentId, string? runId, string? nodeId)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        // Inject W3C traceparent/tracestate from Activity.Current (the invocation span).
        DistributedContextPropagator.Current.Inject(
            Activity.Current, msg,
            static (carrier, name, value) =>
                ((HttpRequestMessage)carrier!).Headers.TryAddWithoutValidation(name, value));
        msg.Headers.TryAddWithoutValidation("X-Vais-Agent-Id", agentId);
        if (runId is not null)
            msg.Headers.TryAddWithoutValidation("X-Vais-Run-Id", runId);
        if (nodeId is not null)
            msg.Headers.TryAddWithoutValidation("X-Vais-Node-Id", nodeId);
        return msg;
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

/// <summary>
/// <see cref="ToolGatewayMiddleware"/> adapter that delegates to an <see cref="HttpContainerHandlerProxy"/>.
/// </summary>
internal sealed class ToolGatewayHandlerProxy : ToolGatewayMiddleware
{
    private readonly HttpContainerHandlerProxy _proxy;

    internal ToolGatewayHandlerProxy(HttpContainerHandlerProxy proxy) => _proxy = proxy;

    /// <inheritdoc />
    public override Task<ToolCallOutcome> InvokeAsync(ToolGatewayContext ctx, Func<Task<ToolCallOutcome>> next, CancellationToken ct = default)
        => _proxy.InvokeToolAsync(ctx, next, ct);
}
