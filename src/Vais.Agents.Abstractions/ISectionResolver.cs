// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Resolves a producer-emitted section list into a canonical, conflict-free, ordered list
/// that's ready for the section window packer and the flattener. Runs once per turn between
/// the <see cref="IContextProvider"/> chain and <c>ISectionWindowPacker</c>.
/// </summary>
/// <remarks>
/// <para>
/// Implementations must enforce two invariants and one ordering rule:
/// </para>
/// <list type="number">
///   <item><description>No two sections share a <see cref="Section.Id"/>. Collision throws <see cref="SectionCollisionException"/>; producers are expected to namespace.</description></item>
///   <item><description>At most one section of kind <see cref="SectionKind.ResponseFormat"/> is present. Multiple throw <see cref="SectionCollisionException"/>.</description></item>
///   <item><description>
///     Sections are sorted by a fixed cross-kind order followed by within-kind <see cref="Section.Order"/>:
///     <see cref="SectionKind.SystemSegment"/> first; then <see cref="SectionKind.UserMessage"/> /
///     <see cref="SectionKind.AssistantMessage"/> / <see cref="SectionKind.ToolMessage"/> as one
///     interleaved group sorted by <see cref="Section.Order"/>; then <see cref="SectionKind.ToolDeclaration"/>;
///     then <see cref="SectionKind.ResponseFormat"/>; then <see cref="SectionKind.Metadata"/>.
///     Within any group, <see cref="Section.Order"/> ascending; null <see cref="Section.Order"/> falls
///     back to the section's position in the input list. Ties in the effective order resolve by the
///     section's position in the input (stable).
///   </description></item>
/// </list>
/// </remarks>
public interface ISectionResolver
{
    /// <summary>
    /// Resolve <paramref name="contributed"/> into the canonical section list.
    /// </summary>
    /// <param name="contributed">Section list as emitted by the <see cref="IContextProvider"/> chain, in registration order. Must not be null; an empty list returns an empty result.</param>
    /// <param name="cancellationToken">Cancellation token honoured by the implementation.</param>
    /// <returns>The resolved, sorted section list.</returns>
    /// <exception cref="SectionCollisionException">Two or more sections share an id, or more than one ResponseFormat section is present.</exception>
    ValueTask<IReadOnlyList<Section>> ResolveAsync(
        IReadOnlyList<Section> contributed,
        CancellationToken cancellationToken = default);
}
