// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.VectorData;

namespace Vais.Agents.Persistence.VectorData.Tests;

/// <summary>
/// Minimal record shape the tests store in the in-memory vector collection. Attributes
/// are the standard <c>Microsoft.Extensions.VectorData</c> decorations consumers use to
/// tell the connector which field is the key, which is queryable data, and which holds
/// the embedding vector.
/// </summary>
public sealed class TestDocumentRecord
{
    [VectorStoreKey]
    public required string Id { get; init; }

    [VectorStoreData]
    public required string Text { get; init; }

    [VectorStoreVector(8)]
    public required ReadOnlyMemory<float> Vector { get; init; }
}
