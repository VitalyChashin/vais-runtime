// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Base class for session-lifecycle seam handlers. Fired when an agent session opens
/// (<see cref="SessionPhase.Opened"/>) and closes (<see cref="SessionPhase.Closing"/>). Observe-only:
/// a session opening/closing has nothing to mutate. The anchor case is session summarization on
/// close — the <c>closing</c> context carries the conversation <see cref="SessionLifecycleContext.History"/>.
/// </summary>
/// <remarks>
/// <para>
/// The consumer is the agent's Orleans grain lifecycle: <c>opened</c> on grain activation, <c>closing</c>
/// on grain deactivation. Because grains deactivate after idle and reactivate on the next message, the
/// phases mean <em>grain activated / grain deactivating</em> — a long idle session produces extra
/// <c>opened</c>/<c>closing</c> pairs.
/// </para>
/// <para>
/// <b>Close is best-effort (P1).</b> Grain deactivation runs on idle-timeout, shutdown, or explicit
/// session removal, but a hard crash skips it — so summarize-on-close is inherently lossy. A hook never
/// breaks the lifecycle: the runtime swallows hook exceptions (logged at WARN); a failing hook does not
/// abort activation or deactivation.
/// </para>
/// <para>Instances must be reentrant — no per-call state in fields.</para>
/// </remarks>
public abstract class SessionLifecycleHook
{
    /// <summary>
    /// Observe a session lifecycle transition. The default is a no-op.
    /// </summary>
    public virtual Task OnSessionAsync(SessionLifecycleContext context, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

/// <summary>
/// The session lifecycle transition passed to a <see cref="SessionLifecycleHook"/>.
/// </summary>
/// <param name="AgentId">The agent the session belongs to.</param>
/// <param name="SessionId">The session id (the per-session key; equals the run id for graph-node sessions).</param>
/// <param name="Phase"><see cref="SessionPhase.Opened"/> or <see cref="SessionPhase.Closing"/>.</param>
/// <param name="TurnCount">The conversation turn count at this transition.</param>
/// <param name="History">
/// The conversation history (role + text per turn), populated on <see cref="SessionPhase.Closing"/>;
/// null on <see cref="SessionPhase.Opened"/>.
/// </param>
public sealed record SessionLifecycleContext(
    string AgentId,
    string SessionId,
    string Phase,
    int TurnCount,
    IReadOnlyList<SessionTurn>? History);

/// <summary>A single conversation turn surfaced to a <see cref="SessionLifecycleHook"/> on close.</summary>
/// <param name="Role">The turn role — <c>user</c>, <c>assistant</c>, <c>system</c>, or <c>tool</c>.</param>
/// <param name="Text">The turn text.</param>
public sealed record SessionTurn(string Role, string Text);

/// <summary>Canonical <see cref="SessionLifecycleContext.Phase"/> values.</summary>
public static class SessionPhase
{
    /// <summary>The session opened (grain activated).</summary>
    public const string Opened = "opened";

    /// <summary>The session is closing (grain deactivating).</summary>
    public const string Closing = "closing";
}
