// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents.Hosting.Orleans;

/// <summary>
/// Client-side <see cref="IAgentSession"/> adapter over an <see cref="IAgentSessionGrain"/>.
/// Compose with <see cref="Core.StatefulAiAgent"/> via
/// <see cref="Core.StatefulAgentOptions.Session"/> to run the turn-loop locally while
/// history is durably owned by the silo grain.
/// </summary>
/// <remarks>
/// <para>
/// <b>Caching.</b> <see cref="History"/> is sync (the <see cref="IAgentSession"/>
/// contract). The first read blocks on a grain RPC to hydrate the cache; subsequent
/// reads return the cached snapshot. The cache is refreshed after
/// <see cref="AppendAsync"/> and <see cref="ResetAsync"/>. Between appends another
/// client's write is invisible until the next local append or a proxy re-creation.
/// </para>
/// <para>
/// <b>Threading.</b> Designed for use from non-grain contexts. Blocking on a grain
/// call from inside another grain's turn would deadlock the single-threaded grain
/// scheduler; in those contexts use <see cref="IAgentSessionGrain"/> directly.
/// </para>
/// </remarks>
public sealed class OrleansAgentSession : IAgentSession
{
    private readonly IAgentSessionGrain _grain;
    private IReadOnlyList<ChatTurn>? _historyCache;

    /// <summary>Create a session proxy bound to a grain. Typically called via <see cref="OrleansAgentRuntime.GetSession"/>.</summary>
    public OrleansAgentSession(IAgentSessionGrain grain, string agentId, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(grain);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _grain = grain;
        AgentId = agentId;
        SessionId = sessionId;
    }

    /// <inheritdoc />
    public string AgentId { get; }

    /// <inheritdoc />
    public string SessionId { get; }

    /// <inheritdoc />
    public IReadOnlyList<ChatTurn> History
    {
        get
        {
            if (_historyCache is null)
            {
                _historyCache = _grain.GetHistoryAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            }
            return _historyCache;
        }
    }

    /// <inheritdoc />
    public async ValueTask AppendAsync(ChatTurn turn, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turn);
        await _grain.AppendAsync(turn).ConfigureAwait(false);
        // Refresh: the grain's history is now authoritative; re-read so a subsequent
        // History access sees the just-appended turn plus any concurrent writer's
        // turns that happened to land first (Orleans serialises, but between our
        // AppendAsync call returning and this read completing, another client could
        // have appended — that's fine; we pick up the full authoritative list).
        _historyCache = await _grain.GetHistoryAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask ResetAsync(CancellationToken cancellationToken = default)
    {
        await _grain.ResetAsync().ConfigureAwait(false);
        _historyCache = Array.Empty<ChatTurn>();
    }
}
