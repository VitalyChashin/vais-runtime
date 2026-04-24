// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;

namespace Vais.Agents.Runtime.Plugins.Python.Tests;

internal sealed record CapturingLogEntry(string Message, IReadOnlyDictionary<string, object?> Scope);

/// <summary>
/// Thread-safe logger that records every emitted log entry along with the active scope state.
/// </summary>
internal sealed class CapturingLogger : ILogger
{
    private readonly List<CapturingLogEntry> _entries = [];
    private readonly object _lock = new();
    private readonly AsyncLocal<Dictionary<string, object?>?> _scope = new();

    internal IReadOnlyList<CapturingLogEntry> Entries
    {
        get { lock (_lock) return [.. _entries]; }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        var prev = _scope.Value;
        if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            _scope.Value = pairs.ToDictionary(p => p.Key, p => p.Value);
        return new ScopeHandle(() => _scope.Value = prev);
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var scope = _scope.Value is not null
            ? new Dictionary<string, object?>(_scope.Value)
            : new Dictionary<string, object?>();
        var entry = new CapturingLogEntry(formatter(state, exception), scope);
        lock (_lock) _entries.Add(entry);
    }

    private sealed class ScopeHandle(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}

/// <summary>
/// <see cref="ILoggerFactory"/> that returns the same <see cref="CapturingLogger"/> for every category.
/// </summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    internal CapturingLogger Logger { get; } = new();
    public void AddProvider(ILoggerProvider provider) { }
    public ILogger CreateLogger(string categoryName) => Logger;
    public void Dispose() { }
}
