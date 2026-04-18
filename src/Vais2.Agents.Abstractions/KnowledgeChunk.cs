// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents;

/// <summary>
/// A single piece of retrieved context returned by <see cref="IKnowledgeRetriever"/>.
/// Immutable, value-equal, serialisable.
/// </summary>
/// <param name="Text">The retrieved text. Non-null; may be empty.</param>
/// <param name="Id">Optional stable identifier (e.g. source document id + offset) for traceability.</param>
/// <param name="Score">Optional similarity score assigned by the retriever. Higher-is-better by convention; semantics are retriever-specific.</param>
public sealed record KnowledgeChunk(string Text, string? Id = null, float? Score = null);
