// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;

namespace Vais2.Agents.Core;

/// <summary>
/// Group-chat orchestrator: rotates through the participant list for up to
/// <c>maxRounds</c> rounds, with every participant seeing the full shared
/// conversation (user task + each prior step, labeled by agent name) as its
/// input. Optionally short-circuits via a <see cref="TerminationPredicate"/>
/// evaluated after every step.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shared-history encoding.</b> Prior <see cref="OrchestrationStep"/>s are
/// encoded as assistant turns of the form <c>"[AgentName] text"</c>. Mixing the
/// speaker's name into the text (rather than using a per-turn role) is the only
/// stack-neutral option: <see cref="ChatTurn"/> has no "author" field beyond
/// <see cref="AgentChatRole"/>, and different providers interpret multi-assistant
/// conversations differently. The prefix keeps things explicit.
/// </para>
/// <para>
/// <b>Termination.</b> Rounds are capped at <c>maxRounds</c>. A supplied
/// <see cref="TerminationPredicate"/> is evaluated after every yielded step; if
/// it returns <c>true</c>, the orchestration stops immediately (does not
/// advance to the next participant even if the current round is unfinished).
/// </para>
/// </remarks>
public sealed class RoundRobinOrchestrator : IAgentOrchestrator
{
    private readonly IReadOnlyList<AgentParticipant> _participants;
    private readonly int _maxRounds;
    private readonly ITerminationCondition? _terminate;

    /// <summary>
    /// Create a round-robin orchestrator with a delegate-based termination predicate.
    /// </summary>
    /// <param name="participants">Participants, cycled in the order given.</param>
    /// <param name="maxRounds">
    /// Maximum number of full rotations through the participant list. Each round
    /// emits <c>participants.Count</c> steps unless terminated early.
    /// </param>
    /// <param name="terminate">
    /// Optional predicate evaluated after each step. Return <c>true</c> to stop
    /// the orchestration immediately. Null means run for the full
    /// <paramref name="maxRounds"/>.
    /// </param>
    /// <exception cref="ArgumentException">Thrown when participants is empty or maxRounds is non-positive.</exception>
    public RoundRobinOrchestrator(
        IReadOnlyList<AgentParticipant> participants,
        int maxRounds,
        TerminationPredicate? terminate = null)
        : this(participants, maxRounds, terminate is null ? null : TerminationConditions.FromPredicate(terminate))
    {
    }

    /// <summary>
    /// Create a round-robin orchestrator with an <see cref="ITerminationCondition"/>.
    /// Preferred for new code — supports async termination checks and composition.
    /// </summary>
    /// <param name="participants">Participants, cycled in the order given.</param>
    /// <param name="maxRounds">
    /// Maximum number of full rotations. Each round emits <c>participants.Count</c> steps
    /// unless terminated early.
    /// </param>
    /// <param name="terminate">
    /// Optional async termination check evaluated after each step. Null means run
    /// for the full <paramref name="maxRounds"/>.
    /// </param>
    /// <exception cref="ArgumentException">Thrown when participants is empty or maxRounds is non-positive.</exception>
    public RoundRobinOrchestrator(
        IReadOnlyList<AgentParticipant> participants,
        int maxRounds,
        ITerminationCondition? terminate)
    {
        ArgumentNullException.ThrowIfNull(participants);
        if (participants.Count == 0)
        {
            throw new ArgumentException("At least one participant is required.", nameof(participants));
        }
        if (maxRounds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRounds), "maxRounds must be positive.");
        }

        _participants = participants;
        _maxRounds = maxRounds;
        _terminate = terminate;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<OrchestrationStep> RunAsync(
        string task,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(task))
        {
            throw new ArgumentException("Task must be non-empty.", nameof(task));
        }

        var steps = new List<OrchestrationStep>();

        for (var round = 0; round < _maxRounds; round++)
        {
            foreach (var participant in _participants)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var request = new CompletionRequest(
                    History: BuildSharedHistory(task, steps),
                    SystemPrompt: participant.SystemPrompt);

                var response = await participant.Provider.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
                var step = new OrchestrationStep(participant.Name, response.Text);
                steps.Add(step);
                yield return step;

                if (_terminate is not null && await _terminate.ShouldTerminateAsync(steps, cancellationToken).ConfigureAwait(false))
                {
                    yield break;
                }
            }
        }
    }

    private static IReadOnlyList<ChatTurn> BuildSharedHistory(string task, IReadOnlyList<OrchestrationStep> steps)
    {
        var turns = new List<ChatTurn>(capacity: steps.Count + 1)
        {
            new(AgentChatRole.User, task),
        };
        foreach (var step in steps)
        {
            turns.Add(new ChatTurn(AgentChatRole.Assistant, $"[{step.AgentName}] {step.Text}"));
        }
        return turns;
    }
}
