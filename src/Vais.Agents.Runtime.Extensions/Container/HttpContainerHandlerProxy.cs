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

    // ── errorInterceptor (single call) ────────────────────────────────────────
    // No self-instrumentation (the composer's InstrumentedErrorInterceptor emits the span). A handler
    // failure here is ALWAYS swallowed (observe-only) regardless of failureMode — an interceptor on
    // the failure path must never mask or replace the original failure (P9).

    internal async Task<ErrorOutcome> InvokeErrorAsync(ErrorContext ctx, CancellationToken ct)
    {
        var callId = Guid.NewGuid().ToString("N");
        var wire = new ErrorContextWire(ctx.AgentId, ctx.RunId, ctx.NodeId, ctx.ErrorType, ctx.ErrorMessage);
        var req = new ErrorInterceptorRequest(callId, wire);
        try
        {
            using var msg = CreateTracedRequest(_preEndpoint, req, ctx.AgentId, ctx.RunId, ctx.NodeId);
            using var resp = await _http.SendAsync(msg, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadFromJsonAsync<ErrorInterceptorResponse>(JsonOptions, ct).ConfigureAwait(false);
            return string.IsNullOrEmpty(body?.Message) ? ErrorOutcome.Observe : new ErrorOutcome(body!.Message);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "error-interceptor handler {Endpoint} failed; agentId={AgentId} runId={RunId}; observing only.",
                _preEndpoint, ctx.AgentId, ctx.RunId);
            return ErrorOutcome.Observe;
        }
    }

    // ── graphNode (node-body wrap, pre/post) ──────────────────────────────────
    // No self-instrumentation: the composer's InstrumentedGraphNodeMiddleware emits the span.

    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyGraphNodeOutput =
        new Dictionary<string, JsonElement>();

    internal async Task<GraphNodeOutcome> InvokeGraphNodeAsync(
        GraphNodeContext ctx, Func<Task<GraphNodeOutcome>> next, CancellationToken ct)
    {
        var callId = Guid.NewGuid().ToString("N");
        var wire = new GraphNodeContextWire(ctx.RunId, ctx.NodeId, ctx.NodeKind, ctx.AgentId, ctx.SuperStep, ctx.Input);
        var preReq = new GraphNodePreRequest(callId, wire);

        GraphNodePreResponse? preResp;
        try
        {
            preResp = await SendPreAsync<GraphNodePreRequest, GraphNodePreResponse>(
                preReq, ctx.AgentId, ctx.RunId, ctx.NodeId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return await HandleGraphNodeFailureAsync(next, ctx, ex).ConfigureAwait(false);
        }

        if (preResp is null)
        {
            _logger.LogWarning(
                "graphNode pre-response null from {Endpoint}; agentId={AgentId} nodeId={NodeId} failureMode={Mode}",
                _preEndpoint, ctx.AgentId, ctx.NodeId, _failureMode);
            return await HandleGraphNodeFailureAsync(next, ctx, null).ConfigureAwait(false);
        }

        if (string.Equals(preResp.Action, "shortCircuit", StringComparison.OrdinalIgnoreCase))
            return new GraphNodeOutcome(preResp.Output ?? EmptyGraphNodeOutput);

        var outcome = await next().ConfigureAwait(false);

        var postReq = new GraphNodePostRequest(callId, preResp.ContinuationToken, outcome.Output);
        var postResp = await TrySendPostAsync<GraphNodePostRequest, GraphNodePostResponse>(
            postReq, ctx.AgentId, ctx.RunId, ctx.NodeId, ct).ConfigureAwait(false);

        if (postResp is { Output: not null } && string.Equals(postResp.Action, "mutate", StringComparison.OrdinalIgnoreCase))
            return new GraphNodeOutcome(postResp.Output);

        return outcome;
    }

    // graphNode never fails a node closed: skip / null pre-response runs the body (handler absent);
    // only an explicit failureMode=fail + transport error propagates. Silently substituting empty
    // output would corrupt graph state.
    private async Task<GraphNodeOutcome> HandleGraphNodeFailureAsync(
        Func<Task<GraphNodeOutcome>> next, GraphNodeContext ctx, Exception? ex)
    {
        if (ex is not null)
            _logger.LogWarning(ex,
                "graphNode handler proxy {Endpoint} failed; agentId={AgentId} nodeId={NodeId} failureMode={Mode}",
                _preEndpoint, ctx.AgentId, ctx.NodeId, _failureMode);

        if (ex is not null && !string.Equals(_failureMode, "skip", StringComparison.OrdinalIgnoreCase))
            throw new ExtensionHandlerProxyException(_preEndpoint, ex);

        return await next().ConfigureAwait(false);
    }

    // ── llmGatewayMiddleware (non-streaming) ──────────────────────────────────
    // No self-instrumentation here: the composer's InstrumentedLlmMiddleware emits the
    // vais.extension.handler.invoke span with the build-time agentId (which the proxy lacks).

    internal async Task<CompletionResponse> InvokeLlmAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next,
        CancellationToken ct)
    {
        var callId = Guid.NewGuid().ToString("N");
        var preReq = new LlmGatewayPreRequest(callId, ToRequestWire(request));

        LlmGatewayPreResponse? preResp;
        try
        {
            preResp = await SendPreAsync<LlmGatewayPreRequest, LlmGatewayPreResponse>(
                preReq, agentId: "", runId: null, nodeId: null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return await HandleLlmFailureAsync(request, next, ex, ct).ConfigureAwait(false);
        }

        if (preResp is null)
        {
            _logger.LogWarning("llm pre-response null from {Endpoint}; failureMode={Mode}", _preEndpoint, _failureMode);
            return await HandleLlmFailureAsync(request, next, null, ct).ConfigureAwait(false);
        }

        if (string.Equals(preResp.Action, "shortCircuit", StringComparison.OrdinalIgnoreCase))
            return ToResponse(preResp.Response);

        var effective = string.Equals(preResp.Action, "mutate", StringComparison.OrdinalIgnoreCase) && preResp.Request is not null
            ? ApplyRequestMutation(request, preResp.Request)
            : request;

        var response = await next(effective, ct).ConfigureAwait(false);

        var postReq = new LlmGatewayPostRequest(callId, preResp.ContinuationToken, ToResponseWire(response));
        var postResp = await TrySendPostAsync<LlmGatewayPostRequest, LlmGatewayPostResponse>(
            postReq, agentId: "", runId: null, nodeId: null, ct).ConfigureAwait(false);

        if (postResp is { Response: not null } && string.Equals(postResp.Action, "mutate", StringComparison.OrdinalIgnoreCase))
            return ApplyResponseMutation(response, postResp.Response);

        return response;
    }

    private async Task<CompletionResponse> HandleLlmFailureAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next,
        Exception? ex, CancellationToken ct)
    {
        if (ex is not null)
            _logger.LogWarning(ex, "llm handler proxy {Endpoint} failed; failureMode={Mode}", _preEndpoint, _failureMode);

        // failureMode=skip, or a null pre-response: behave as if the handler were absent (call the model).
        // Failing closed on the LLM path would break the agent turn; only an explicit fail + transport
        // error propagates.
        if (ex is not null && !string.Equals(_failureMode, "skip", StringComparison.OrdinalIgnoreCase))
            throw new ExtensionHandlerProxyException(_preEndpoint, ex);

        return await next(request, ct).ConfigureAwait(false);
    }

    // ── LLM wire mapping (tools are read-only; ITool cannot round-trip back into the request) ──

    private static LlmRequestWire ToRequestWire(CompletionRequest r) => new(
        Messages: r.History.Select(ToMessageWire).ToArray(),
        SystemPrompt: r.SystemPrompt,
        Temperature: r.Temperature,
        MaxTokens: r.MaxTokens,
        Tools: r.Tools?.Select(t => new LlmToolDeclWire(t.Name, t.Description, t.ParametersSchema)).ToArray(),
        ResponseFormat: r.ResponseFormat is null
            ? null
            : new LlmResponseFormatWire(r.ResponseFormat.Schema, r.ResponseFormat.SchemaName, r.ResponseFormat.Strict),
        AgentId: "",
        RunId: null);

    private static LlmMessageWire ToMessageWire(ChatTurn t) => new(
        Role: RoleToWire(t.Role),
        Content: t.Text,
        ToolCalls: t.ToolCalls?.Select(tc => new LlmToolCallWire(tc.CallId, tc.ToolName, tc.Arguments)).ToArray(),
        ToolCallId: t.ToolCallId);

    private static ChatTurn WireToTurn(LlmMessageWire m) => new(
        RoleFromWire(m.Role),
        m.Content ?? "",
        ToolCalls: m.ToolCalls?.Select(tc => new ToolCallRequest(tc.Name, tc.Arguments, tc.Id)).ToArray(),
        ToolCallId: m.ToolCallId);

    private static CompletionRequest ApplyRequestMutation(CompletionRequest original, LlmRequestWire w) => original with
    {
        History = w.Messages.Select(WireToTurn).ToArray(),
        SystemPrompt = w.SystemPrompt,
        Temperature = (float?)w.Temperature,
        MaxTokens = w.MaxTokens,
        ResponseFormat = w.ResponseFormat is null
            ? original.ResponseFormat
            : new ResponseFormatSpec(w.ResponseFormat.Schema, w.ResponseFormat.Name, w.ResponseFormat.Strict),
        // Tools intentionally preserved from the original — ITool cannot round-trip.
    };

    private static LlmResponseWire ToResponseWire(CompletionResponse r) =>
        new(r.Text, r.PromptTokens, r.CompletionTokens);

    private static CompletionResponse ToResponse(LlmResponseWire? w) =>
        w is null
            ? new CompletionResponse(string.Empty)
            : new CompletionResponse(w.Text, PromptTokens: w.PromptTokens, CompletionTokens: w.CompletionTokens);

    private static CompletionResponse ApplyResponseMutation(CompletionResponse original, LlmResponseWire w) =>
        original with { Text = w.Text, PromptTokens = w.PromptTokens, CompletionTokens = w.CompletionTokens };

    private static string RoleToWire(AgentChatRole role) => role switch
    {
        AgentChatRole.System    => "system",
        AgentChatRole.Assistant => "assistant",
        AgentChatRole.Tool      => "tool",
        _                       => "user",
    };

    private static AgentChatRole RoleFromWire(string role) => role switch
    {
        "system"    => AgentChatRole.System,
        "assistant" => AgentChatRole.Assistant,
        "tool"      => AgentChatRole.Tool,
        _           => AgentChatRole.User,
    };

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

/// <summary>
/// <see cref="LlmGatewayMiddleware"/> adapter that delegates the non-streaming path to an
/// <see cref="HttpContainerHandlerProxy"/>. Streaming passes through unchanged (the pre/post
/// HTTP protocol cannot express a streaming call).
/// </summary>
internal sealed class LlmGatewayHandlerProxy : LlmGatewayMiddleware
{
    private readonly HttpContainerHandlerProxy _proxy;

    internal LlmGatewayHandlerProxy(HttpContainerHandlerProxy proxy) => _proxy = proxy;

    /// <inheritdoc />
    protected override Task<CompletionResponse> InvokeAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next,
        CancellationToken cancellationToken)
        => _proxy.InvokeLlmAsync(request, next, cancellationToken);

    /// <inheritdoc />
    protected override IAsyncEnumerable<CompletionUpdate> InvokeStreamAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next,
        CancellationToken cancellationToken)
        => next(request, cancellationToken);
}

/// <summary>
/// <see cref="ErrorInterceptor"/> adapter that delegates to an <see cref="HttpContainerHandlerProxy"/>.
/// </summary>
internal sealed class ErrorInterceptorHandlerProxy : ErrorInterceptor
{
    private readonly HttpContainerHandlerProxy _proxy;

    internal ErrorInterceptorHandlerProxy(HttpContainerHandlerProxy proxy) => _proxy = proxy;

    /// <inheritdoc />
    public override Task<ErrorOutcome> OnErrorAsync(ErrorContext context, CancellationToken cancellationToken = default)
        => _proxy.InvokeErrorAsync(context, cancellationToken);
}

/// <summary>
/// <see cref="GraphNodeMiddleware"/> adapter that delegates to an <see cref="HttpContainerHandlerProxy"/>.
/// </summary>
internal sealed class GraphNodeHandlerProxy : GraphNodeMiddleware
{
    private readonly HttpContainerHandlerProxy _proxy;

    internal GraphNodeHandlerProxy(HttpContainerHandlerProxy proxy) => _proxy = proxy;

    /// <inheritdoc />
    public override Task<GraphNodeOutcome> InvokeAsync(GraphNodeContext context, Func<Task<GraphNodeOutcome>> next, CancellationToken cancellationToken = default)
        => _proxy.InvokeGraphNodeAsync(context, next, cancellationToken);
}
