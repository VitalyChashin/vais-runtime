// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents.Core;

/// <summary>
/// Identity <see cref="IContextWindowPacker"/>. Returns the candidate unchanged.
/// Default when <see cref="StatefulAgentOptions.ContextWindowPacker"/> is null.
/// </summary>
public sealed class NoopContextWindowPacker : IContextWindowPacker
{
    /// <summary>Shared singleton instance. Stateless.</summary>
    public static readonly NoopContextWindowPacker Instance = new();

    private NoopContextWindowPacker() { }

    /// <inheritdoc />
    public ValueTask<CompletionRequest> PackAsync(
        CompletionRequest candidate,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(candidate);
}
