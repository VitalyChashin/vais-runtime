// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Core;

/// <summary>
/// Default <see cref="IAgentContextAccessor"/> backed by <see cref="AsyncLocal{T}"/>.
/// Works across awaits, async method flows, and <see cref="Task.Run(Action)"/> captures.
/// </summary>
/// <remarks>
/// Use <see cref="Push"/> around a logical scope:
/// <code>
/// using (accessor.Push(new AgentContext(UserId: "alice"))) { await agent.AskAsync(...); }
/// </code>
/// The disposed token restores the previous value.
/// </remarks>
public sealed class AsyncLocalAgentContextAccessor : IAgentContextAccessor
{
    private static readonly AsyncLocal<AgentContext?> _current = new();

    /// <inheritdoc />
    public AgentContext Current => _current.Value ?? AgentContext.Empty;

    /// <summary>
    /// Push a new context for the current async flow. Dispose the returned scope to
    /// restore the previous value.
    /// </summary>
    public IDisposable Push(AgentContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var previous = _current.Value;
        _current.Value = context;
        return new Restorer(previous);
    }

    private sealed class Restorer : IDisposable
    {
        private AgentContext? _previous;
        private bool _disposed;

        public Restorer(AgentContext? previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _current.Value = _previous;
            _previous = null;
            _disposed = true;
        }
    }
}
