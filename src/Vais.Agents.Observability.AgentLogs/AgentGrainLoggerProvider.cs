// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;

namespace Vais.Agents.Observability.AgentLogs;

/// <summary>
/// <see cref="ILoggerProvider"/> that intercepts log calls carrying an <c>AgentId</c> scope
/// (pushed by <c>AiAgentGrain</c> via <c>_logger.BeginScope("{AgentId}", agentId)</c>) and
/// forwards them to <see cref="IAgentLogSink"/>.
/// </summary>
/// <remarks>
/// Registered as a singleton <see cref="ILoggerProvider"/>. For all log entries that lack an
/// <c>AgentId</c> scope, <see cref="AgentGrainLogger.Log"/> is a no-op.
/// </remarks>
public sealed class AgentGrainLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly IAgentLogSink _sink;
    private IExternalScopeProvider _scopeProvider = NoOpScopeProvider.Instance;

    /// <summary>Creates a new provider backed by the given sink.</summary>
    public AgentGrainLoggerProvider(IAgentLogSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
    }

    /// <inheritdoc />
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) =>
        _scopeProvider = scopeProvider ?? NoOpScopeProvider.Instance;

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new AgentGrainLogger(_sink, _scopeProvider);

    /// <inheritdoc />
    public void Dispose() { }

    private sealed class AgentGrainLogger : ILogger
    {
        private readonly IAgentLogSink _sink;
        private readonly IExternalScopeProvider _scopeProvider;

        internal AgentGrainLogger(IAgentLogSink sink, IExternalScopeProvider scopeProvider)
        {
            _sink = sink;
            _scopeProvider = scopeProvider;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            string? agentId = null;
            _scopeProvider.ForEachScope((scope, _) =>
            {
                if (agentId is not null) return;
                if (scope is IReadOnlyList<KeyValuePair<string, object?>> kvList)
                {
                    foreach (var kv in kvList)
                    {
                        if (kv.Key == "AgentId" && kv.Value is string id)
                        {
                            agentId = id;
                            break;
                        }
                    }
                }
            }, (object?)null);

            if (agentId is null) return;

            try
            {
                var message = formatter(state, exception);
                _sink.Add(new AgentLogEntry(
                    Guid.NewGuid().ToString("N"),
                    agentId,
                    RunId: null,
                    DateTimeOffset.UtcNow,
                    logLevel.ToString(),
                    message,
                    "grain"));
            }
            catch { }
        }
    }
}

file sealed class NoOpScopeProvider : IExternalScopeProvider
{
    internal static readonly NoOpScopeProvider Instance = new();
    public void ForEachScope<TState>(Action<object?, TState> callback, TState state) { }
    public IDisposable Push(object? state) => NullDisposable.Instance;
}

file sealed class NullDisposable : IDisposable
{
    internal static readonly NullDisposable Instance = new();
    public void Dispose() { }
}
