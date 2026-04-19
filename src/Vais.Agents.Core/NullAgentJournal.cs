// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;

namespace Vais.Agents.Core;

/// <summary>
/// No-op <see cref="IAgentJournal"/>. Appends are silently dropped; reads always
/// yield nothing. The default when <see cref="StatefulAgentOptions.Journal"/> is
/// unset — preserves pre-v0.5 behaviour (no durable resume, no replay cache).
/// </summary>
public sealed class NullAgentJournal : IAgentJournal
{
    /// <summary>Shared singleton instance. Stateless.</summary>
    public static readonly NullAgentJournal Instance = new();

    private NullAgentJournal() { }

    /// <inheritdoc />
    public ValueTask AppendAsync(JournalEntry entry, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async IAsyncEnumerable<JournalEntry> ReadAsync(
        string runId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    /// <inheritdoc />
    public ValueTask ClearAsync(string runId, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}
