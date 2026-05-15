// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Builds the base system prompt for a turn from one or more parts. When a composer
/// is supplied (see <c>StatefulAgentOptions.SystemPromptComposer</c>), the host
/// calls <see cref="ComposeAsync"/> per turn and uses the returned string as the
/// base system prompt — the plain <c>SystemPrompt</c> string is ignored in that case.
/// Context-provider <c>SystemPromptAddendum</c> values still concatenate on top.
/// </summary>
/// <remarks>
/// Typically paired with <see cref="ISystemPromptContributor"/> via the shipped
/// <c>AggregatingSystemPromptComposer</c>, which orders contributors by priority
/// and joins their outputs. Custom composers can ignore contributors entirely and
/// build the prompt however they like.
/// </remarks>
public interface ISystemPromptComposer
{
    /// <summary>
    /// Return the composed system prompt for this turn, or null to produce no base
    /// prompt. Return values are used verbatim — callers supply their own leading
    /// or trailing whitespace if they want any.
    /// </summary>
    ValueTask<string?> ComposeAsync(AgentContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Section-shaped composition entry point. Overriding implementations emit one
    /// <see cref="SectionKind.SystemSegment"/> section per logical part (e.g. per
    /// <see cref="ISystemPromptContributor"/>) so the section resolver, packer, and
    /// telemetry surface can operate per-contributor instead of seeing one concatenated
    /// string. The default implementation wraps <see cref="ComposeAsync"/>: it emits a
    /// single <c>system.composed</c> section when <see cref="ComposeAsync"/> returns a
    /// non-empty string, or an empty list otherwise — so custom composers keep working
    /// without recompilation.
    /// </summary>
    ValueTask<IReadOnlyList<Section>> ComposeSectionsAsync(AgentContext context, CancellationToken cancellationToken = default)
        => DefaultComposeSectionsAsync(this, context, cancellationToken);

    /// <summary>
    /// Default <see cref="ComposeSectionsAsync"/> implementation extracted so derived classes
    /// can opt back into the wrap-single-section behaviour explicitly.
    /// </summary>
    static async ValueTask<IReadOnlyList<Section>> DefaultComposeSectionsAsync(
        ISystemPromptComposer composer,
        AgentContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(composer);
        var text = await composer.ComposeAsync(context, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<Section>();
        }

        return new[]
        {
            new Section(
                "system.composed",
                SectionKind.SystemSegment,
                new TextPayload(text),
                ProducerId: composer.GetType().Name),
        };
    }
}
