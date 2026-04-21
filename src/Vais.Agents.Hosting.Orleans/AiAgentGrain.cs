// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

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
    private readonly Func<string, StatefulAgentOptions> _optionsFactory;
    private readonly ILoggerFactory _loggerFactory;
    private StatefulAiAgent? _agent;

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
        Func<string, StatefulAgentOptions>? optionsFactory = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        _defaultProvider = provider;
        _optionsFactory = optionsFactory ?? (id => new StatefulAgentOptions { AgentName = id });
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var id = this.GetPrimaryKeyString();
        var supplied = _optionsFactory(id);

        var provider = supplied.CompletionProvider ?? _defaultProvider
            ?? throw new InvalidOperationException(
                $"Agent grain '{id}' activated but no ICompletionProvider is available. " +
                "Either register a silo-wide ICompletionProvider in DI (v0.16 pattern) or " +
                "configure the manifest instantiator (v0.17 Pillar B) so the translator " +
                "supplies a per-agent provider via StatefulAgentOptions.CompletionProvider.");

        var seeded = new StatefulAgentOptions
        {
            AgentName = supplied.AgentName ?? id,
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
        return base.OnActivateAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string> AskAsync(string userMessage)
    {
        var agent = EnsureAgent();
        var reply = await agent.AskAsync(userMessage);

        _state.State.History = agent.History.ToList();
        _state.State.SystemPrompt = agent.SystemPrompt;
        await _state.WriteStateAsync();

        return reply;
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
        await _state.WriteStateAsync();
    }

    /// <inheritdoc />
    public async Task DeleteAsync()
    {
        await _state.ClearStateAsync();
        _agent = null;
        DeactivateOnIdle();
    }

    private StatefulAiAgent EnsureAgent() =>
        _agent ?? throw new InvalidOperationException("Grain is not activated.");
}
