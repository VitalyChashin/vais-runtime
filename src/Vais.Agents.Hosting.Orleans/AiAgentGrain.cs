// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vais.Agents.Core;

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
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AiAgentGrain> _logger;
    private string? _agentId;
    private IAiAgent? _agent;

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
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        _defaultProvider = provider;
        _optionsFactory = optionsFactory ?? ((id, _) => ValueTask.FromResult(new StatefulAgentOptions { AgentName = id }));
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<AiAgentGrain>();
    }

    /// <inheritdoc />
    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _agentId = this.GetPrimaryKeyString();
        using var scope = _logger.BeginScope("{AgentId}", _agentId);
        using var activity = OrleansDiagnostics.ActivitySource.StartActivity("grain.activate");
        activity?.SetTag(AgenticTags.AgentName, _agentId);

        _logger.LogDebug("Grain activating");

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
            if (_state.State.SystemPrompt is { } persistedPrompt)
            {
                _agent.SystemPrompt = persistedPrompt;
            }
            // v0.24 — restore opaque state blob for Python (and any future) plugin agents.
            if (_agent is IOpaqueStateCarrier carrier && _state.State.OpaqueState is { } blob)
            {
                carrier.OpaqueState = blob;
            }
            _logger.LogInformation("Grain activated in {ElapsedMs}ms, mode=plugin", sw.ElapsedMilliseconds);
            await base.OnActivateAsync(cancellationToken);
            return;
        }

        var provider = supplied.CompletionProvider ?? _defaultProvider
            ?? throw new InvalidOperationException(
                $"Agent grain '{_agentId}' activated but no ICompletionProvider is available. " +
                "Either register a silo-wide ICompletionProvider in DI (v0.16 pattern) or " +
                "configure the manifest instantiator (v0.17 Pillar B) so the translator " +
                "supplies a per-agent provider via StatefulAgentOptions.CompletionProvider.");

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
            InitialHistory = _state.State.History.Count == 0 ? null : _state.State.History.ToArray(),
        };

        _agent = new StatefulAiAgent(provider, seeded, _loggerFactory.CreateLogger<StatefulAiAgent>());
        _logger.LogInformation("Grain activated in {ElapsedMs}ms, mode=declarative", sw.ElapsedMilliseconds);
        await base.OnActivateAsync(cancellationToken);
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

        var sw = Stopwatch.StartNew();
        var reply = await agent.AskAsync(userMessage);

        _state.State.History = agent.History.ToList();
        _state.State.SystemPrompt = agent.SystemPrompt;
        if (agent is IOpaqueStateCarrier carrier)
            _state.State.OpaqueState = carrier.OpaqueState;
        await _state.WriteStateAsync();

        _logger.LogDebug("AskAsync completed in {ElapsedMs}ms", sw.ElapsedMilliseconds);
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

        if (agent is not IStreamingAiAgent streamingAgent)
            throw new NotSupportedException(
                $"Agent grain '{_agentId}' does not support streaming — the inner agent " +
                $"({agent.GetType().Name}) does not implement IStreamingAiAgent.");

        var sw = Stopwatch.StartNew();
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
                if (evt is TurnFailed)
                    activity?.SetStatus(ActivityStatusCode.Error);
            }
            yield return evt;
        }
        _logger.LogDebug("StreamAgentAsync completed in {ElapsedMs}ms", sw.ElapsedMilliseconds);
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
        await _state.ClearStateAsync();
        _agent = null;
        DeactivateOnIdle();
    }

    private IAiAgent EnsureAgent() =>
        _agent ?? throw new InvalidOperationException("Grain is not activated.");
}
