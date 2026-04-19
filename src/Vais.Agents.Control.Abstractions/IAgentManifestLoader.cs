// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// Loads <see cref="AgentManifest"/> records from a textual source — YAML, JSON, or
/// a future extension format. Pure: returns parsed + validated manifests, does not
/// reconcile them against a registry. Reconciliation belongs on the HTTP server
/// (apply-style) or a future K8s operator.
/// </summary>
/// <remarks>
/// <para>
/// All methods throw <see cref="AgentManifestValidationException"/> when the
/// content parses but fails the schema rules (required fields, label-key format,
/// semver, duplicate ids, mutually-exclusive field combinations). Parse errors
/// propagate as the underlying parser's exception (e.g. <c>YamlException</c>,
/// <c>JsonException</c>) — the loader does not wrap them.
/// </para>
/// </remarks>
public interface IAgentManifestLoader
{
    /// <summary>Parse one or more manifests from an in-memory string. Multi-document inputs are supported (YAML <c>---</c> separator / JSON arrays).</summary>
    ValueTask<IReadOnlyList<AgentManifest>> LoadFromStringAsync(string content, CancellationToken cancellationToken = default);

    /// <summary>Parse manifests from a single file on disk.</summary>
    ValueTask<IReadOnlyList<AgentManifest>> LoadFromFileAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Parse manifests from every file in <paramref name="directory"/> matching
    /// <paramref name="searchPattern"/>, concatenated. Duplicate ids across files
    /// throw <see cref="AgentManifestValidationException"/>. Files are processed
    /// in <c>ordinal</c> filename order for deterministic results.
    /// </summary>
    ValueTask<IReadOnlyList<AgentManifest>> LoadFromDirectoryAsync(string directory, string searchPattern, CancellationToken cancellationToken = default);
}
