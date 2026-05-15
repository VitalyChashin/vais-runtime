// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Thrown by <see cref="ISectionResolver"/> implementations when the contributed section list
/// cannot be resolved into a canonical form. Two distinct failure modes share this exception:
/// (1) two or more sections share the same <see cref="Section.Id"/>; (2) more than one section
/// of kind <see cref="SectionKind.ResponseFormat"/> is present (the response-format slot is a
/// singleton). Both indicate a producer-chain misconfiguration — the fix is to namespace section
/// ids or remove duplicate response-format contributions.
/// </summary>
public sealed class SectionCollisionException : Exception
{
    /// <summary>The section id (or singleton kind name) that triggered the conflict.</summary>
    public string OffendingKey { get; }

    /// <summary>The producer ids of the conflicting sections, in registration order.</summary>
    public IReadOnlyList<string?> ProducerIds { get; }

    /// <summary>Construct an exception describing a section conflict.</summary>
    /// <param name="offendingKey">The duplicated <see cref="Section.Id"/>, or the singleton kind name (e.g. <c>ResponseFormat</c>).</param>
    /// <param name="producerIds">The producer ids of the conflicting sections.</param>
    /// <param name="message">Human-readable description of the conflict.</param>
    public SectionCollisionException(string offendingKey, IReadOnlyList<string?> producerIds, string message)
        : base(message)
    {
        OffendingKey = offendingKey;
        ProducerIds = producerIds;
    }
}
