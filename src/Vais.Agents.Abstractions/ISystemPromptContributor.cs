// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// One part of a multi-part system prompt. Contributors are aggregated by an
/// <see cref="ISystemPromptComposer"/> (the shipped <c>AggregatingSystemPromptComposer</c>
/// orders by <see cref="Priority"/> ascending and joins non-null / non-empty outputs
/// with <c>"\n\n"</c>).
/// </summary>
public interface ISystemPromptContributor
{
    /// <summary>
    /// Ordering hint. Lower values run earlier — contributor with <c>Priority = 0</c>
    /// appears at the top of the composed prompt. Matches ASP.NET middleware ordering.
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Return this contributor's slice of the prompt, or null/empty to contribute nothing.
    /// </summary>
    ValueTask<string?> ContributeAsync(AgentContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Section id used when the contributor's output is emitted as a <see cref="Section"/> by
    /// <c>AggregatingSystemPromptComposer</c>. Defaults to <c>"system.&lt;type-name-kebab-case&gt;"</c>
    /// — override when registering multiple contributors of the same type so the section resolver
    /// can tell them apart.
    /// </summary>
    string SectionId => DefaultSectionId(GetType());

    /// <summary>
    /// Default section id factory exposed so test helpers and custom composers can reproduce the
    /// convention without re-implementing the kebab-case rule.
    /// </summary>
    static string DefaultSectionId(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var name = type.Name;
        if (name.Length == 0)
        {
            return "system.contributor";
        }

        var sb = new System.Text.StringBuilder("system.", capacity: name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var ch = name[i];
            if (char.IsUpper(ch))
            {
                if (i > 0) sb.Append('-');
                sb.Append(char.ToLowerInvariant(ch));
            }
            else if (char.IsLetterOrDigit(ch) || ch is '_' or '-')
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append('_');
            }
        }
        return sb.ToString();
    }
}
