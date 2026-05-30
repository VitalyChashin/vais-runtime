// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vais.Agents.Core;
using Vais.Agents.Runtime.Extensions;

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Default <see cref="IAiAgentGrain"/> implementation. Wraps a neutral
/// <see cref="StatefulAiAgent"/> and persists its mutating state
/// (<see cref="IAiAgent.History"/>, <see cref="IAiAgent.SystemPrompt"/>) to an
/// <see cref="IPersistentState{TState}"/> store configured by the host.
/// </summary>
/// <remarks>
/// <para>
/// Each grain activation constructs a fresh <see cref="StatefulAiAgent"/>, seeding
/// it from the persisted state via <see cref="StatefulAgentOptions.InitialHistory"/>.
/// After every <see cref="AskAsync"/> the grain copies the agent's history back to
/// state and writes it — one round-trip to the store per turn.
/// </para>
/// <para>
/// Options (filters, resilience, tool registry, usage sink) come from the
/// <see cref="Func{String, StatefulAgentOptions}"/> factory registered in silo DI;
/// the factory receives the grain's string key. The completion provider is taken
/// from silo DI as a singleton.
/// </para>
/// </remarks>
public sealed class AiAgentGrain : Grain, IAiAgentGrain
{
    /// <summary>
    /// Storage name under which <see cref="AiAgentGrainState"/> is persisted. Hosts
    /// must register an <c>IGrainStorage</c> with this exact name (e.g.
    /// <c>siloBuilder.AddMemoryGrainStorage(AiAgentGrain.StorageName)</c>).
    /// </summary>
    public const string StorageName = "vais.agents";

    private readonly IPersistentState<AiAgentGrainState> _state;
    private readonly ICompletionProvider? _defaultProvider;
    private readonly Func<string, CancellationToken, ValueTask<StatefulAgentOptions>> _optionsFactory;
    private readonly IAgentContextAccessor? _contextAccessor;
    private readonly OrleansAgentContextAccessor? _grainContextReader;
    private readonly IAgentContextSetter? _contextSetter;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AiAgentGrain> _logger;
    private readonly IExtensionChainComposer? _extensionChainComposer;
    private string? _agentId;
    private string? _sessionId;
    private bool _sessionClosed;
    private IAiAgent? _agent;
    // Non-null only in plugin mode — signals that AskAsync should record tool calls from history delta.
    private ToolGatewayMiddleware[]? _pluginToolGatewayMiddleware;
    private AgentInputMiddleware[]? _inputMiddleware;

    /// <summary>
    /// Grain constructor. Dependencies resolved from silo DI.
    /// </summary>
    /// <remarks>
    /// Since v0.17 Pillar B, <paramref name="provider"/> is optional — the
    /// translator-supplied <see cref="StatefulAgentOptions.CompletionProvider"/>
    /// takes precedence when set (it derives the provider from the manifest's
    /// <c>ModelSpec</c> per agent). Hosts still registering a DI-wide
    /// provider as the v0.16 pattern did get the old fall-back behaviour.
    /// At activation the grain throws if neither source supplies a provider.
    /// </remarks>
    public AiAgentGrain(
        [PersistentState("state", StorageName)] IPersistentState<AiAgentGrainState> state,
        ICompletionProvider? provider = null,
        Func<string, CancellationToken, ValueTask<StatefulAgentOptions>>? optionsFactory = null,
        ILoggerFactory? loggerFactory = null,
        IAgentContextAccessor? contextAccessor = null,
        IExtensionChainComposer? extensionChainComposer = null,
        OrleansAgentContextAccessor? grainContextReader = null,
        IAgentContextSetter? contextSetter = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        _defaultProvider = provider;
        _optionsFactory = optionsFactory ?? ((id, _) => ValueTask.FromResult(new StatefulAgentOptions { AgentName = id }));
        _contextAccessor = contextAccessor;
        _grainContextReader = grainContextReader;
        _contextSetter = contextSetter;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<AiAgentGrain>();
        _extensionChainComposer = extensionChainComposer;
    }

    /// <inheritdoc />
    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var grainKey = this.GetPrimaryKeyString();
        // Session-scoped grains have key "agentId/sessionId" — extract the logical
        // agentId for manifest lookup; the full key is the grain's unique identity.
        var slash = grainKey.IndexOf('/');
        _agentId = slash > 0 ? grainKey[..slash] : grainKey;
        _sessionId = slash > 0 ? grainKey[(slash + 1)..] : string.Empty;
        using var scope = _logger.BeginScope("{AgentId}", _agentId);
        // OnActivateAsync runs on the Orleans grain scheduler with no ambient Activity.Current.
        // Skip the span when there's no parent — an orphan root trace adds no observability value.
        using var activity = Activity.Current != null
            ? OrleansDiagnostics.ActivitySource.StartActivity("grain.activate")
            : null;
        activity?.SetTag(AgenticTags.AgentName, _agentId);

        _logger.LogDebug("Grain activating — agentId={AgentId}", _agentId);

        var sw = Stopwatch.StartNew();
        StatefulAgentOptions supplied;
        try
        {
            supplied = await _optionsFactory(_agentId, cancellationToken);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Options factory failed after {ElapsedMs}ms", sw.ElapsedMilliseconds);
            throw;
        }

        _logger.LogDebug("Options factory returned in {ElapsedMs}ms", sw.ElapsedMilliseconds);

        // v0.18 Pillar C plugin branch: translator already constructed a
        // concrete IAiAgent (plugin-authored). The grain treats it verbatim —
        // persistence still runs off IAiAgent.History, but we do not re-seed
        // history here since the plugin factory owns its own session hydration.
        if (supplied.Agent is not null)
        {
            _agent = supplied.Agent;
            _pluginToolGatewayMiddleware = supplied.ToolGatewayMiddleware.Count > 0
                ? supplied.ToolGatewayMiddleware.ToArray()
                : null;
            _inputMiddleware = supplied.InputMiddleware.Count > 0
                ? supplied.InputMiddleware.ToArray()
                : null;
            if (_state.State.SystemPrompt is { } persistedPrompt)
            {
                _agent.SystemPrompt = persistedPrompt;
            }
            // v0.24 — restore opaque state blob for Python (and any future) plugin agents.
            if (_agent is IOpaqueStateCarrier carrier && _state.State.OpaqueState is { } blob)
            {
                carrier.OpaqueState = blob;
            }
            if (_agent is IAgentGrainStateConsumer consumer)
            {
                consumer.SetGrainState(_state.State);
            }
            _logger.LogInformation("Grain activated — agentId={AgentId} mode=plugin elapsedMs={ElapsedMs}", _agentId, sw.ElapsedMilliseconds);
            await FireSessionLifecycleAsync(SessionPhase.Opened, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            await base.OnActivateAsync(cancellationToken);
            return;
        }

        var provider = supplied.CompletionProvider ?? _defaultProvider
            ?? throw new InvalidOperationException(
                $"Agent grain '{_agentId}' activated but no ICompletionProvider is available. " +
                "Either register a silo-wide ICompletionProvider in DI (v0.16 pattern) or " +
                "configure the manifest instantiator (v0.17 Pillar B) so the translator " +
                "supplies a per-agent provider via StatefulAgentOptions.CompletionProvider.");

        // Fetch extension-bound middleware chains and merge after the statically-registered chains.
        IReadOnlyList<AgentInputMiddleware> extInputChain = Array.Empty<AgentInputMiddleware>();
        IReadOnlyList<AgentOutputMiddleware> extOutputChain = Array.Empty<AgentOutputMiddleware>();
        IReadOnlyList<ToolGatewayMiddleware> extToolChain = Array.Empty<ToolGatewayMiddleware>();
        IReadOnlyList<LlmGatewayMiddleware> extLlmChain = Array.Empty<LlmGatewayMiddleware>();
        IReadOnlyList<ErrorInterceptor> extErrorChain = Array.Empty<ErrorInterceptor>();
        if (_extensionChainComposer is not null)
        {
            extInputChain = await _extensionChainComposer.GetInputChainAsync(_agentId!, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            extOutputChain = await _extensionChainComposer.GetOutputChainAsync(_agentId!, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            extToolChain = await _extensionChainComposer.GetToolChainAsync(_agentId!, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            extLlmChain = await _extensionChainComposer.GetLlmChainAsync(_agentId!, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            extErrorChain = await _extensionChainComposer.GetErrorInterceptorChainAsync(_agentId!, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        }

        var mergedInputMiddleware = supplied.InputMiddleware.Count > 0 || extInputChain.Count > 0
            ? [.. supplied.InputMiddleware, .. extInputChain]
            : (IReadOnlyList<AgentInputMiddleware>)Array.Empty<AgentInputMiddleware>();

        _inputMiddleware = mergedInputMiddleware.Count > 0 ? mergedInputMiddleware.ToArray() : null;

        var mergedOutputMiddleware = supplied.OutputMiddleware.Count > 0 || extOutputChain.Count > 0
            ? [.. supplied.OutputMiddleware, .. extOutputChain]
            : (IReadOnlyList<AgentOutputMiddleware>)Array.Empty<AgentOutputMiddleware>();

        var mergedToolMiddleware = supplied.ToolGatewayMiddleware.Count > 0 || extToolChain.Count > 0
            ? [.. supplied.ToolGatewayMiddleware, .. extToolChain]
            : (IReadOnlyList<ToolGatewayMiddleware>)Array.Empty<ToolGatewayMiddleware>();

        var mergedGatewayMiddleware = supplied.GatewayMiddleware.Count > 0 || extLlmChain.Count > 0
            ? [.. supplied.GatewayMiddleware, .. extLlmChain]
            : (IReadOnlyList<LlmGatewayMiddleware>)Array.Empty<LlmGatewayMiddleware>();

        var mergedErrorInterceptors = supplied.ErrorInterceptors.Count > 0 || extErrorChain.Count > 0
            ? [.. supplied.ErrorInterceptors, .. extErrorChain]
            : (IReadOnlyList<ErrorInterceptor>)Array.Empty<ErrorInterceptor>();

        var seeded = new StatefulAgentOptions
        {
            AgentName = supplied.AgentName ?? _agentId,
            SystemPrompt = _state.State.SystemPrompt ?? supplied.SystemPrompt,
            Filters = supplied.Filters,
            UsageSink = supplied.UsageSink,
            ContextAccessor = supplied.ContextAccessor,
            ResiliencePipeline = supplied.ResiliencePipeline,
            ToolRegistry = supplied.ToolRegistry,
            InputGuardrails = supplied.InputGuardrails,
            OutputGuardrails = supplied.OutputGuardrails,
            ToolGuardrails = supplied.ToolGuardrails,
            Budget = supplied.Budget,
            GatewayMiddleware = mergedGatewayMiddleware,
            ToolGatewayMiddleware = mergedToolMiddleware,
            ErrorInterceptors = mergedErrorInterceptors,
            InputMiddleware = mergedInputMiddleware,
            OutputMiddleware = mergedOutputMiddleware,
            // Section telemetry sinks (OTel/Langfuse section tags, Prometheus, event bus) resolved by
            // the translator. Must be propagated through the grain re-seed or declarative agents emit
            // no per-section breakdown — keep this in sync when adding new option fields.
            SectionTelemetrySinks = supplied.SectionTelemetrySinks,
            InitialHistory = _state.State.History.Count == 0 ? null : _state.State.History.ToArray(),
        };

        if (_state.State.History.Count > 0)
            _logger.LogDebug("Grain rehydrated history — agentId={AgentId} turns={Turns}", _agentId, _state.State.History.Count);

        _agent = new StatefulAiAgent(provider, seeded, _loggerFactory.CreateLogger<StatefulAiAgent>());
        _logger.LogInformation(
            "Grain activated — agentId={AgentId} mode=declarative elapsedMs={ElapsedMs} gateway-middleware=[{GatewayMiddleware}] tool-middleware=[{ToolMiddleware}]",
            _agentId,
            sw.ElapsedMilliseconds,
            string.Join(", ", seeded.GatewayMiddleware?.Select(m => m.GetType().Name) ?? Array.Empty<string>()),
            string.Join(", ", seeded.ToolGatewayMiddleware?.Select(m => m.GetType().Name) ?? Array.Empty<string>()));
        await FireSessionLifecycleAsync(SessionPhase.Opened, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        await base.OnActivateAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Grain deactivating — agentId={AgentId} reason={Reason}", _agentId, reason.ReasonCode);
        if (reason.ReasonCode == DeactivationReasonCode.ShuttingDown)
            _logger.LogInformation("Grain deactivating on shutdown — agentId={AgentId}", _agentId);
        // Fire closing here only for the idle/shutdown path. The explicit-removal path (DeleteAsync)
        // already fired it BEFORE clearing state — re-firing here would deliver empty history.
        // Run on a bounded token of our own, not the deactivation token. The independent 5 s bound
        // must stay < HostOptions.ShutdownTimeout (default 30 s) so it finishes within the host budget.
        if (!_sessionClosed)
        {
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await FireSessionLifecycleAsync(SessionPhase.Closing, closeCts.Token).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
        await base.OnDeactivateAsync(reason, cancellationToken);
    }

    /// <summary>
    /// Fires the <c>sessionLifecycle</c> extension chain (best-effort) for the given phase. On
    /// <see cref="SessionPhase.Closing"/> the conversation history is projected to <see cref="SessionTurn"/>
    /// for summarize-on-close. A hook failure is logged at WARN and never aborts the grain lifecycle.
    /// </summary>
    private async Task FireSessionLifecycleAsync(string phase, CancellationToken cancellationToken)
    {
        if (_extensionChainComposer is null || _agentId is null)
            return;

        IReadOnlyList<SessionLifecycleHook> hooks;
        try
        {
            hooks = await _extensionChainComposer.GetSessionLifecycleChainAsync(_agentId, cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "sessionLifecycle chain resolution failed; agentId={AgentId} phase={Phase}", _agentId, phase);
            return;
        }

        if (hooks.Count == 0)
            return;

        IReadOnlyList<SessionTurn>? history = null;
        if (string.Equals(phase, SessionPhase.Closing, StringComparison.Ordinal) && _state.State.History.Count > 0)
        {
            history = _state.State.History
                .Select(t => new SessionTurn(t.Role.ToString().ToLowerInvariant(), t.Text))
                .ToArray();
        }

        var ctx = new SessionLifecycleContext(
            _agentId, _sessionId ?? string.Empty, phase, _state.State.History.Count, history);

        foreach (var hook in hooks)
        {
            try
            {
                await hook.OnSessionAsync(ctx, cancellationToken)
                    .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "sessionLifecycle hook failed; agentId={AgentId} phase={Phase}", _agentId, phase);
            }
        }
    }

    /// <inheritdoc />
    public async Task<string> AskAsync(string userMessage)
    {
        var agent = EnsureAgent();
        using var scope = _logger.BeginScope("{AgentId}", _agentId);
        var parentCtx = ActivityPropagation.ReadContext();
        using var activity = OrleansDiagnostics.ActivitySource.StartActivity(
            "grain.ask", ActivityKind.Internal, parentCtx);
        activity?.SetTag(AgenticTags.AgentName, _agentId);
        var runId = ActivityPropagation.ReadGraphRunId();
        if (runId is not null)
            activity?.SetTag("graph.run_id", runId);
        activity?.SetTag("gen_ai.prompt", userMessage);

        _logger.LogDebug("Turn starting — agentId={AgentId} runId={RunId} messageLen={MessageLen}", _agentId, runId, userMessage.Length);
        var sw = Stopwatch.StartNew();
        var prevHistoryCount = _pluginToolGatewayMiddleware is not null ? agent.History.Count : 0;

        // P12O-7: run input middleware chain over the inbound message before delegating to the agent.
        var inputMessage = userMessage;
        if (_inputMiddleware is { Length: > 0 })
        {
            var inputCtx = new AgentInputContext
            {
                AgentId = _agentId!,
                RunId = runId,
                Message = userMessage,
            };
            await RunInputMiddlewareAsync(inputCtx, _inputMiddleware, CancellationToken.None);
            inputMessage = inputCtx.Message;
        }

        var incomingContext = _grainContextReader?.Current ?? AgentContext.Empty;
        string reply;
        try
        {
            using (_contextSetter?.Push(incomingContext))
            {
                reply = await agent.AskAsync(inputMessage);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Turn failed — agentId={AgentId} runId={RunId} elapsedMs={ElapsedMs}", _agentId, runId, sw.ElapsedMilliseconds);
            throw;
        }
        activity?.SetTag("gen_ai.completion", reply);

        if (_pluginToolGatewayMiddleware is { } mw && mw.Length > 0)
            RecordPluginToolCalls(agent.History, prevHistoryCount, mw);

        _state.State.History = agent.History.ToList();
        _state.State.SystemPrompt = agent.SystemPrompt;
        if (agent is IOpaqueStateCarrier carrier)
            _state.State.OpaqueState = carrier.OpaqueState;
        await _state.WriteStateAsync();

        _logger.LogDebug("Turn completed — agentId={AgentId} runId={RunId} elapsedMs={ElapsedMs}", _agentId, runId, sw.ElapsedMilliseconds);
        return reply;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentEvent> StreamAgentAsync(
        string userMessage,
        AgentContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var agent = EnsureAgent();
        using var scope = _logger.BeginScope("{AgentId}", _agentId);
        using var activity = OrleansDiagnostics.ActivitySource.StartActivity("grain.stream");
        activity?.SetTag(AgenticTags.AgentName, _agentId);
        // Match grain.ask's prompt/completion tagging so Langfuse shows the user message and
        // final assistant text on the root span (and hence trace-level input/output).
        activity?.SetTag("gen_ai.prompt", userMessage);

        // Bridge HTTP-injected CorrelationId into Orleans RequestContext so gateway middleware
        // can read it via OrleansAgentContextAccessor (which reads RequestContext, not the context parameter).
        if (context.CorrelationId is not null && RequestContext.Get(AgenticTags.CorrelationId) is null)
            RequestContext.Set(AgenticTags.CorrelationId, context.CorrelationId);

        if (agent is not IStreamingAiAgent streamingAgent)
            throw new NotSupportedException(
                $"Agent grain '{_agentId}' does not support streaming — the inner agent " +
                $"({agent.GetType().Name}) does not implement IStreamingAiAgent.");

        _logger.LogDebug("Turn starting (streaming) — agentId={AgentId} messageLen={MessageLen}", _agentId, userMessage.Length);
        var sw = Stopwatch.StartNew();
        using var _ctx = _contextSetter?.Push(context);
        await foreach (var evt in streamingAgent.StreamAsync(userMessage, context, cancellationToken))
        {
            if (evt is TurnCompleted or TurnFailed)
            {
                _state.State.History = agent.History.ToList();
                _state.State.SystemPrompt = agent.SystemPrompt;
                if (agent is IOpaqueStateCarrier carrier)
                    _state.State.OpaqueState = carrier.OpaqueState;
                try
                {
                    await _state.WriteStateAsync();
                }
                catch (Exception ex)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    _logger.LogError(ex, "Failed to persist agent state after streaming turn");
                }
                if (evt is TurnCompleted tc && !string.IsNullOrEmpty(tc.AssistantText))
                {
                    activity?.SetTag("gen_ai.completion", tc.AssistantText);
                }
                if (evt is TurnFailed)
                {
                    activity?.SetStatus(ActivityStatusCode.Error);
                    _logger.LogError("Turn failed (streaming) — agentId={AgentId} elapsedMs={ElapsedMs}", _agentId, sw.ElapsedMilliseconds);
                }
            }
            yield return evt;
        }
        _logger.LogDebug("Turn completed (streaming) — agentId={AgentId} elapsedMs={ElapsedMs}", _agentId, sw.ElapsedMilliseconds);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ChatTurn>> GetHistoryAsync()
    {
        var agent = EnsureAgent();
        return Task.FromResult<IReadOnlyList<ChatTurn>>(agent.History.ToArray());
    }

    /// <inheritdoc />
    public Task<string?> GetSystemPromptAsync()
    {
        var agent = EnsureAgent();
        return Task.FromResult(agent.SystemPrompt);
    }

    /// <inheritdoc />
    public async Task SetSystemPromptAsync(string? value)
    {
        var agent = EnsureAgent();
        agent.SystemPrompt = value;
        _state.State.SystemPrompt = value;
        await _state.WriteStateAsync();
    }

    /// <inheritdoc />
    public async Task ResetAsync()
    {
        var agent = EnsureAgent();
        agent.Reset();
        _state.State.History = new List<ChatTurn>();
        _state.State.OpaqueState = null;
        await _state.WriteStateAsync();
    }

    /// <inheritdoc />
    public async Task DeleteAsync()
    {
        // Fire the session-close hooks with the conversation history intact, BEFORE clearing state
        // (ClearStateAsync wipes _state.State.History, which OnDeactivateAsync would otherwise read as
        // empty). This is the run-completion eviction path — the common close trigger — so it must
        // deliver real history for summarize-on-close.
        using (var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            await FireSessionLifecycleAsync(SessionPhase.Closing, closeCts.Token).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
        _sessionClosed = true;
        await _state.ClearStateAsync();
        _agent = null;
        DeactivateOnIdle();
    }

    /// <inheritdoc />
    public Task RequestDeactivationAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    private IAiAgent EnsureAgent() =>
        _agent ?? throw new InvalidOperationException("Grain is not activated.");

    private static Task RunInputMiddlewareAsync(
        AgentInputContext ctx,
        AgentInputMiddleware[] middleware,
        CancellationToken cancellationToken)
    {
        Func<Task> chain = () => Task.CompletedTask;
        for (var i = middleware.Length - 1; i >= 0; i--)
        {
            var mw = middleware[i];
            var inner = chain;
            chain = () => mw.InvokeAsync(ctx, inner, cancellationToken);
        }
        return chain();
    }

    private void RecordPluginToolCalls(
        IReadOnlyList<ChatTurn> history,
        int prevCount,
        ToolGatewayMiddleware[] middleware)
    {
        var agentCtx = _contextAccessor?.Current ?? AgentContext.Empty;
        for (var i = prevCount; i < history.Count; i++)
        {
            var turn = history[i];
            if (turn.ToolCalls is not { Count: > 0 } calls)
                continue;
            foreach (var call in calls)
            {
                var gwCtx = new ToolGatewayContext(call.ToolName, call.CallId, call.Arguments, agentCtx);
                Func<Task<ToolCallOutcome>> chain = () => Task.FromResult(new ToolCallOutcome(call.CallId, string.Empty));
                for (var j = middleware.Length - 1; j >= 0; j--)
                {
                    var mw = middleware[j];
                    var inner = chain;
                    chain = () => mw.InvokeAsync(gwCtx, inner, CancellationToken.None);
                }
                _ = chain();
            }
        }
    }
}
