// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vais.Agents.Core;

namespace Vais.Agents.Runtime.Plugins.Container;

/// <summary>
/// <see cref="IAiAgent"/> backed by a container image that speaks the IP-1 HTTP protocol.
/// Implements <see cref="IAgentGrainStateConsumer"/> so <c>AiAgentGrain</c> can seed history
/// after factory creation without changing <see cref="IAgentHandlerFactory.CreateAsync"/>.
/// </summary>
internal sealed class ContainerAgentShim
    : IAiAgent, IStreamingAiAgent, IOpaqueStateCarrier, IAgentGrainStateConsumer
{
    private static readonly ActivitySource _activitySource =
        new("Vais.Agents.Runtime.Plugins.Container", "1.0.0");

    private readonly IContainerSupervisor _supervisor;
    private readonly HttpClient _invokeClient;
    private readonly IAgentPreprocessor[] _preprocessors;
    private readonly AgentManifest _manifest;
    private readonly ICallTokenService _callTokenService;
    private readonly string _internalLlmGatewayUrl;
    private readonly string _internalToolGatewayUrl;
    private readonly int _invokeTimeoutSeconds;
    private readonly ContainerSessionTokenConfig? _sessionConfig;
    private readonly ILogger _logger;

    private IAgentGrainStateView? _grainState;
    private string? _opaqueStateJson;
    private readonly List<ChatTurn> _history = new();

    internal ContainerAgentShim(
        IContainerSupervisor supervisor,
        HttpClient invokeClient,
        IAgentPreprocessor[] preprocessors,
        AgentManifest manifest,
        ICallTokenService callTokenService,
        string internalLlmGatewayUrl,
        string internalToolGatewayUrl,
        int invokeTimeoutSeconds,
        ContainerSessionTokenConfig? sessionConfig,
        ILogger logger)
    {
        _supervisor = supervisor;
        _invokeClient = invokeClient;
        _preprocessors = preprocessors;
        _manifest = manifest;
        _callTokenService = callTokenService;
        _internalLlmGatewayUrl = internalLlmGatewayUrl;
        _internalToolGatewayUrl = internalToolGatewayUrl;
        _invokeTimeoutSeconds = invokeTimeoutSeconds;
        _sessionConfig = sessionConfig;
        _logger = logger;
        Session = new InMemoryAgentSession(manifest.Id);
    }

    // IAgentGrainStateConsumer
    public void SetGrainState(IAgentGrainStateView grainState)
    {
        _grainState = grainState;
        _history.Clear();
        _history.AddRange(grainState.History);
        _opaqueStateJson = grainState.OpaqueState;
    }

    // IOpaqueStateCarrier
    string? IOpaqueStateCarrier.OpaqueState
    {
        get => _opaqueStateJson;
        set => _opaqueStateJson = value;
    }

    // IAiAgent
    public string? SystemPrompt { get; set; }
    public IAgentSession Session { get; }
    public IReadOnlyList<ChatTurn> History => _history;

    public void Reset()
    {
        _history.Clear();
        _opaqueStateJson = null;
    }

    public async Task<string> AskAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);

        using var activity = _activitySource.StartActivity("container.agent.ask", ActivityKind.Internal);
        activity?.SetTag("vais.agent.name", _manifest.Id);
        activity?.SetTag("gen_ai.prompt", userMessage);
        var graphRunId = Activity.Current?.GetTagItem("graph.run_id") as string;
        if (graphRunId is not null) activity?.SetTag("graph.run_id", graphRunId);

        await EnsureLiveAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<ChatTurn> messages = [new ChatTurn(AgentChatRole.User, userMessage)];

        if (_grainState is { } gs && _preprocessors.Length > 0)
        {
            var ctx = new AgentPreprocessorContext(
                _manifest.Id,
                Session.SessionId,
                _manifest,
                gs,
                BuildOperationContext(graphRunId));
            messages = await RunPreprocessorChainAsync(ctx, messages, cancellationToken).ConfigureAwait(false);
        }

        var callToken = MintCallToken(graphRunId ?? "", out var renewTokenUrl);
        var request = BuildInvokeRequest(messages, callToken, graphRunId, renewTokenUrl: renewTokenUrl);

        var response = await InvokeWithRetryAsync(messages, request, cancellationToken).ConfigureAwait(false);

        _history.Add(new ChatTurn(AgentChatRole.User, userMessage));
        _history.Add(BuildAssistantTurn(response));
        _opaqueStateJson = SerialiseOpaqueState(response.OpaqueState);

        activity?.SetTag("gen_ai.completion", response.AssistantMessage);
        return response.AssistantMessage;
    }

    public async IAsyncEnumerable<AgentEvent> StreamAsync(
        string userMessage,
        AgentContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);

        using var activity = _activitySource.StartActivity("container.agent.stream", ActivityKind.Internal);
        activity?.SetTag("vais.agent.name", _manifest.Id);
        var graphRunId = Activity.Current?.GetTagItem("graph.run_id") as string;

        await EnsureLiveAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<ChatTurn> messages = [new ChatTurn(AgentChatRole.User, userMessage)];

        if (_grainState is { } gs && _preprocessors.Length > 0)
        {
            var ctx = new AgentPreprocessorContext(
                _manifest.Id, Session.SessionId, _manifest, gs,
                BuildOperationContext(graphRunId));
            messages = await RunPreprocessorChainAsync(ctx, messages, cancellationToken).ConfigureAwait(false);
        }

        var callToken = MintCallToken(graphRunId ?? "", out var renewTokenUrl);
        var request = BuildInvokeRequest(messages, callToken, graphRunId, renewTokenUrl: renewTokenUrl);

        var start = DateTimeOffset.UtcNow;
        yield return new TurnStarted(start, context, userMessage);

        // Collect SSE events into a buffer first: C# does not allow yield inside try-with-catch.
        var collected = await CollectStreamAsync(request, cancellationToken).ConfigureAwait(false);

        if (collected.Error is OperationCanceledException)
            yield break;

        if (collected.Error is not null)
        {
            yield return new TurnFailed(DateTimeOffset.UtcNow, context,
                collected.Error.GetType().Name, collected.Error.Message,
                DateTimeOffset.UtcNow - start);
            yield break;
        }

        foreach (var text in collected.DeltaTexts)
            yield return new CompletionDelta(DateTimeOffset.UtcNow, context, text);

        if (collected.FinalResponse is null)
        {
            yield return new TurnFailed(DateTimeOffset.UtcNow, context,
                "StreamError", "No final response from container.", DateTimeOffset.UtcNow - start);
            yield break;
        }

        _history.Add(new ChatTurn(AgentChatRole.User, userMessage));
        _history.Add(BuildAssistantTurn(collected.FinalResponse));
        _opaqueStateJson = SerialiseOpaqueState(collected.FinalResponse.OpaqueState);

        yield return new TurnCompleted(
            DateTimeOffset.UtcNow, context, collected.FinalResponse.AssistantMessage,
            ModelId: null,
            PromptTokens: collected.FinalResponse.Usage?.InputTokens,
            CompletionTokens: collected.FinalResponse.Usage?.OutputTokens,
            Duration: DateTimeOffset.UtcNow - start);
    }

    private sealed record StreamCollectResult(
        List<string> DeltaTexts,
        PluginInvokeResponse? FinalResponse,
        Exception? Error);

    private async Task<StreamCollectResult> CollectStreamAsync(
        PluginInvokeRequest request, CancellationToken ct)
    {
        var deltas = new List<string>();
        PluginInvokeResponse? finalResponse = null;

        try
        {
            using var httpReq = new HttpRequestMessage(HttpMethod.Post, "/v1/stream")
            {
                Content = JsonContent.Create(request, options: ContainerJsonOptions.Default),
            };
            httpReq.Headers.Accept.Add(
                new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

            using var httpResp = await _invokeClient.SendAsync(
                httpReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (!httpResp.IsSuccessStatusCode)
            {
                var rawBody = await httpResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                PluginErrorResponse? errorBody = null;
                try { errorBody = JsonSerializer.Deserialize<PluginErrorResponse>(rawBody, ContainerJsonOptions.Default); }
                catch { /* fall through */ }
                errorBody ??= new PluginErrorResponse
                {
                    ErrorType = "InternalError",
                    ErrorMessage = $"HTTP {(int)httpResp.StatusCode}",
                    DiagnosticTail = rawBody.Length > 500 ? rawBody[..500] : rawBody,
                };
                throw new ContainerInvokeException(
                    httpResp.StatusCode, errorBody.ErrorType, errorBody.ErrorMessage, errorBody.DiagnosticTail);
            }

            using var stream = await httpResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await foreach (var ev in ReadSseEventsAsync(stream, ct))
            {
                if (ev.Event == "delta")
                {
                    using var doc = JsonDocument.Parse(ev.Data);
                    if (doc.RootElement.TryGetProperty("text", out var textEl))
                        deltas.Add(textEl.GetString() ?? "");
                }
                else if (ev.Event == "done")
                {
                    finalResponse = JsonSerializer.Deserialize<PluginInvokeResponse>(
                        ev.Data, ContainerJsonOptions.Default);
                }
            }

            return new StreamCollectResult(deltas, finalResponse, null);
        }
        catch (Exception ex)
        {
            return new StreamCollectResult(deltas, finalResponse, ex);
        }
    }

    private async Task EnsureLiveAsync(CancellationToken ct)
    {
        try
        {
            var resp = await _invokeClient.GetAsync("/health", ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return;
        }
        catch (HttpRequestException) { }

        _logger.LogWarning(
            "[{Urn}] Container plugin '{Name}' health check failed; restarting.",
            ContainerPluginUrns.HealthCheckFailed, _manifest.Id);
        await _supervisor.DrainAndReplaceAsync(null, ct).ConfigureAwait(false);
    }

    private async Task<PluginInvokeResponse> InvokeWithRetryAsync(
        IReadOnlyList<ChatTurn> originalMessages,
        PluginInvokeRequest request,
        CancellationToken ct)
    {
        try
        {
            return await PostInvokeAsync(request, ct).ConfigureAwait(false);
        }
        catch (ContainerInvokeException ex) when (ex.StatusCode == HttpStatusCode.UnprocessableContent
            && ex.ErrorType == "OpaqueStateDeserializationError")
        {
            _logger.LogWarning(
                "[{Urn}] Container plugin '{Name}' returned OpaqueStateDeserializationError — retrying with fresh state.",
                ContainerPluginUrns.OpaqueStateDeserializationError, _manifest.Id);
            _opaqueStateJson = null;
            var retryRequest = BuildInvokeRequest(originalMessages, request.Context.CallToken, request.Context.RunId,
                opaqueState: null, renewTokenUrl: request.Context.RenewTokenUrl);
            try
            {
                return await PostInvokeAsync(retryRequest, ct).ConfigureAwait(false);
            }
            catch (ContainerInvokeException retryEx) when (retryEx.StatusCode == HttpStatusCode.UnprocessableContent
                && retryEx.ErrorType == "OpaqueStateDeserializationError")
            {
                throw new OpaqueStateDeserializationException(_manifest.Id);
            }
        }
    }

    private async Task<PluginInvokeResponse> PostInvokeAsync(PluginInvokeRequest request, CancellationToken ct)
    {
        HttpResponseMessage httpResp;
        try
        {
            httpResp = await _invokeClient.PostAsJsonAsync("/v1/invoke", request, ContainerJsonOptions.Default, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[{Urn}] Container invoke network error — agentId={AgentId} runId={RunId}",
                ContainerPluginUrns.InvokeNetworkError, _manifest.Id, request.Context.RunId);
            throw;
        }

        if (httpResp.IsSuccessStatusCode)
        {
            var result = await httpResp.Content
                .ReadFromJsonAsync<PluginInvokeResponse>(ContainerJsonOptions.Default, ct)
                .ConfigureAwait(false);
            return result ?? throw new InvalidOperationException("Empty 2xx body from container invoke");
        }

        string rawBody = "";
        PluginErrorResponse? errorBody = null;
        try
        {
            rawBody = await httpResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            errorBody = JsonSerializer.Deserialize<PluginErrorResponse>(rawBody, ContainerJsonOptions.Default);
        }
        catch { /* fall through */ }

        errorBody ??= new PluginErrorResponse
        {
            ErrorType = "InternalError",
            ErrorMessage = $"Container returned non-JSON error body (HTTP {(int)httpResp.StatusCode})",
            DiagnosticTail = rawBody.Length > 500 ? rawBody[..500] : rawBody,
        };

        _logger.LogError(
            "[{Urn}] Container invoke failed — agentId={AgentId} runId={RunId} " +
            "status={Status} errorType={ErrorType} diagnosticTail={DiagnosticTail}",
            UrnForErrorType(errorBody.ErrorType), _manifest.Id, request.Context.RunId,
            (int)httpResp.StatusCode, errorBody.ErrorType, errorBody.DiagnosticTail);

        throw new ContainerInvokeException(
            httpResp.StatusCode, errorBody.ErrorType, errorBody.ErrorMessage, errorBody.DiagnosticTail);
    }

    private static string UrnForErrorType(string errorType) => errorType switch
    {
        "LlmGatewayError" => ContainerPluginUrns.LlmGatewayError,
        "ToolError" => ContainerPluginUrns.ToolError,
        "Timeout" => ContainerPluginUrns.Timeout,
        "OpaqueStateDeserializationError" => ContainerPluginUrns.OpaqueStateDeserializationError,
        _ => ContainerPluginUrns.InvokeFailed,
    };

    /// <summary>
    /// Mints the call token for one invoke. Session-mode plugins (config present) get a short,
    /// renewable token plus the renewal URL; short-turn plugins get a single full-TTL token and
    /// no renewal URL.
    /// </summary>
    private string MintCallToken(string runId, out string? renewTokenUrl)
    {
        if (_sessionConfig is { } sc)
        {
            renewTokenUrl = sc.RenewTokenUrl;
            return _callTokenService.Generate(runId, _manifest.Id, sc.RenewTokenTtlSeconds);
        }

        renewTokenUrl = null;
        return _callTokenService.Generate(runId, _manifest.Id, _invokeTimeoutSeconds + 30);
    }

    private PluginInvokeRequest BuildInvokeRequest(
        IReadOnlyList<ChatTurn> messages,
        string callToken,
        string? runId,
        JsonElement? opaqueState = default,
        string? renewTokenUrl = null)
    {
        var stateElement = opaqueState ?? ParseOpaqueState(_opaqueStateJson);
        return new PluginInvokeRequest
        {
            AgentId = _manifest.Id,
            SessionId = Session.SessionId,
            Messages = messages.Select(ChatTurnToPluginMessage).ToArray(),
            LlmGatewayUrl = _internalLlmGatewayUrl,
            ToolGatewayUrl = _internalToolGatewayUrl,
            OpaqueState = stateElement,
            TimeoutSeconds = _invokeTimeoutSeconds,
            Context = new PluginRequestContext
            {
                Traceparent = Activity.Current?.Id,
                RunId = runId,
                CorrelationId = null,
                CallToken = callToken,
                RenewTokenUrl = renewTokenUrl,
            }
        };
    }

    private static PluginMessage ChatTurnToPluginMessage(ChatTurn turn) => new()
    {
        Role = turn.Role switch
        {
            AgentChatRole.System => "system",
            AgentChatRole.User => "user",
            AgentChatRole.Assistant => "assistant",
            AgentChatRole.Tool => "tool",
            _ => turn.Role.ToString().ToLowerInvariant(),
        },
        Content = string.IsNullOrEmpty(turn.Text) ? null : turn.Text,
        ToolCalls = turn.ToolCalls?.Select(tc => new PluginToolCall
        {
            Id = tc.CallId ?? "",
            Name = tc.ToolName,
            Arguments = tc.Arguments,
        }).ToArray(),
        ToolCallId = turn.ToolCallId,
    };

    private static ChatTurn BuildAssistantTurn(PluginInvokeResponse response)
    {
        IReadOnlyList<ToolCallRequest>? toolCalls = null;
        if (response.Journal is { Count: > 0 } journal)
        {
            var calls = new List<ToolCallRequest>(journal.Count);
            for (var i = 0; i < journal.Count; i++)
            {
                var entry = journal[i];
                using var doc = JsonDocument.Parse(entry.InputJson ?? "{}");
                calls.Add(new ToolCallRequest(entry.ToolName, doc.RootElement.Clone(), entry.ToolCallId));
            }
            toolCalls = calls;
        }
        return new ChatTurn(AgentChatRole.Assistant, response.AssistantMessage, ToolCalls: toolCalls);
    }

    private static JsonElement? ParseOpaqueState(string? json)
    {
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch { return null; }
    }

    private static string? SerialiseOpaqueState(JsonElement? element)
    {
        if (element is null) return null;
        return element.Value.ValueKind == JsonValueKind.Null ? null : element.Value.GetRawText();
    }

    private async Task<IReadOnlyList<ChatTurn>> RunPreprocessorChainAsync(
        AgentPreprocessorContext ctx,
        IReadOnlyList<ChatTurn> messages,
        CancellationToken ct)
    {
        foreach (var p in _preprocessors)
            messages = await p.ProcessAsync(ctx, messages, ct).ConfigureAwait(false);
        return messages;
    }

    private static AgentContext BuildOperationContext(string? graphRunId) =>
        new AgentContext { RunId = graphRunId };

    private static async IAsyncEnumerable<SseEvent> ReadSseEventsAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream);
        string? eventName = null;
        var dataLines = new System.Text.StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;

            if (line.StartsWith(':')) continue; // heartbeat comment

            if (line.StartsWith("event:", StringComparison.Ordinal))
                eventName = line["event:".Length..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal))
                dataLines.Append(line["data:".Length..].Trim());
            else if (line.Length == 0 && eventName is not null)
            {
                yield return new SseEvent(eventName, dataLines.ToString());
                eventName = null;
                dataLines.Clear();
            }
        }
    }
}

internal readonly record struct SseEvent(string Event, string Data);

/// <summary>
/// Session-mode token wiring for a container plugin (present iff <c>spec.sessionTtlSeconds</c> is set).
/// When null the shim runs in short-turn mode: one full-TTL token per invoke, no renewal, no lease.
/// </summary>
/// <param name="SessionTtlSeconds">Maximum lifetime of one session; the invoke-lease ceiling.</param>
/// <param name="RenewTokenTtlSeconds">Lifetime of each short token the plugin renews before expiry.</param>
/// <param name="RenewTokenUrl">Absolute URL the plugin SDK POSTs to for a fresh token.</param>
internal sealed record ContainerSessionTokenConfig(
    int SessionTtlSeconds,
    int RenewTokenTtlSeconds,
    string RenewTokenUrl);
