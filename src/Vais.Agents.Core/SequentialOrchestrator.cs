// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;

namespace Vais.Agents.Core;

/// <summary>
/// Pipeline orchestrator: runs participants once each, in order, passing each
/// participant's output as the input to the next. The first participant sees the
/// user task; subsequent participants see the previous participant's assistant
/// text verbatim as their user turn.
/// </summary>
/// <remarks>
/// <para>
/// Participants are <em>not</em> given a shared group-chat view — if you want
/// every participant to see every prior turn labeled by author, use
/// <see cref="RoundRobinOrchestrator"/> instead. Sequential is for transformation
/// pipelines (translator → summariser → critic) where each stage operates on the
/// output of the previous.
/// </para>
/// <para>
/// Completes after the last participant. No termination predicate — the pipeline
/// length is fixed at construction.
/// </para>
/// </remarks>
public sealed class SequentialOrchestrator : IAgentOrchestrator
{
    private readonly IReadOnlyList<AgentParticipant> _participants;

    /// <summary>
    /// Create a pipeline over the given participants. The order is preserved.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the participant list is empty.</exception>
    public SequentialOrchestrator(IReadOnlyList<AgentParticipant> participants)
    {
        ArgumentNullException.ThrowIfNull(participants);
        if (participants.Count == 0)
        {
            throw new ArgumentException("At least one participant is required.", nameof(participants));
        }
        _participants = participants;
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

        var currentInput = task;
        foreach (var participant in _participants)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var request = new CompletionRequest(
                History: new[] { new ChatTurn(AgentChatRole.User, currentInput) },
                SystemPrompt: participant.SystemPrompt);

            var response = await participant.Provider.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
            yield return new OrchestrationStep(participant.Name, response.Text);
            currentInput = response.Text;
        }
    }
}
