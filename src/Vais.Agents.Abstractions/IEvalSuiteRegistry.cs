// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Read/write access to the registered eval suite manifests.
/// </summary>
public interface IEvalSuiteRegistry
{
    /// <summary>Enumerate all registered eval suite manifests, optionally filtered by label-key prefix.</summary>
    IAsyncEnumerable<EvalSuiteManifest> ListAsync(string? labelPrefix = null, CancellationToken ct = default);

    /// <summary>Retrieve a suite manifest by id. <paramref name="version"/> null ⇒ latest. Null on miss.</summary>
    ValueTask<EvalSuiteManifest?> GetAsync(string id, string? version = null, CancellationToken ct = default);

    /// <summary>Register or overwrite a suite manifest (upsert semantics).</summary>
    ValueTask UpsertAsync(EvalSuiteManifest manifest, CancellationToken ct = default);

    /// <summary>Remove a suite manifest by id and version. No-op when not found.</summary>
    ValueTask RemoveAsync(string id, string version, CancellationToken ct = default);
}
