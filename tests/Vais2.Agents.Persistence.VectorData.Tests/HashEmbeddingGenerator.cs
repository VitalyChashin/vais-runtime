// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;

namespace Vais2.Agents.Persistence.VectorData.Tests;

/// <summary>
/// Deterministic in-memory <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> for tests.
/// Derives a fixed-dimension float vector from SHA256 of the input text so that the
/// same text always produces the same vector and distinct texts almost always produce
/// distinct vectors. Not suitable for anything other than tests.
/// </summary>
public sealed class HashEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private const int Dimensions = 8;

    public EmbeddingGeneratorMetadata Metadata { get; } =
        new(providerName: "hash-fake", defaultModelId: "hash-sha256-8d");

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var embeddings = new GeneratedEmbeddings<Embedding<float>>(
            values.Select(v => new Embedding<float>(Compute(v))).ToArray());
        return Task.FromResult(embeddings);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(EmbeddingGeneratorMetadata) ? Metadata : null;

    public void Dispose() { }

    internal static ReadOnlyMemory<float> Compute(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var floats = new float[Dimensions];
        for (var i = 0; i < Dimensions; i++)
        {
            // Turn pairs of bytes into a float in [-1, 1].
            var raw = (short)((bytes[i * 2] << 8) | bytes[i * 2 + 1]);
            floats[i] = raw / 32768f;
        }
        // Normalise so cosine distance behaves sanely.
        var norm = MathF.Sqrt(floats.Sum(f => f * f));
        if (norm > 0f)
        {
            for (var i = 0; i < Dimensions; i++)
            {
                floats[i] /= norm;
            }
        }
        return floats;
    }
}
