// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text;

namespace Vais.Agents.Core;

/// <summary>
/// Default <see cref="ISystemPromptComposer"/>. Orders its injected contributors by
/// <see cref="ISystemPromptContributor.Priority"/> ascending, invokes each one, and
/// joins the non-null / non-empty results with <c>"\n\n"</c>. Returns null when no
/// contributor produces any text (so the host falls through to its usual null-prompt
/// handling rather than sending an empty string).
/// </summary>
public sealed class AggregatingSystemPromptComposer : ISystemPromptComposer
{
    private readonly IReadOnlyList<ISystemPromptContributor> _ordered;

    /// <summary>
    /// Create a composer over a set of contributors. The set is sorted once at
    /// construction; subsequent <see cref="ComposeAsync"/> calls iterate in the
    /// pre-sorted order.
    /// </summary>
    public AggregatingSystemPromptComposer(IEnumerable<ISystemPromptContributor> contributors)
    {
        ArgumentNullException.ThrowIfNull(contributors);
        _ordered = contributors.OrderBy(c => c.Priority).ToArray();
    }

    /// <inheritdoc />
    public async ValueTask<string?> ComposeAsync(AgentContext context, CancellationToken cancellationToken = default)
    {
        if (_ordered.Count == 0)
        {
            return null;
        }

        StringBuilder? sb = null;
        foreach (var contributor in _ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var piece = await contributor.ContributeAsync(context, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(piece))
            {
                continue;
            }

            if (sb is null)
            {
                sb = new StringBuilder(piece);
            }
            else
            {
                sb.Append("\n\n").Append(piece);
            }
        }

        return sb?.ToString();
    }
}
