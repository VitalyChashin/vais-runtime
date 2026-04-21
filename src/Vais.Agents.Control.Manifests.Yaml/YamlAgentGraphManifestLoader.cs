// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Manifests;

/// <summary>
/// YAML-authored graph-manifest loader. Normalises YAML to JSON (preserving key
/// order) via <see cref="YamlAgentManifestLoader.YamlToJson"/> and delegates to
/// <see cref="JsonAgentGraphManifestLoader"/>. One format, two wire shapes, one
/// validator.
/// </summary>
public sealed class YamlAgentGraphManifestLoader
{
    private readonly JsonAgentGraphManifestLoader _inner = new();

    /// <summary>Parse graph manifests from a YAML string. Agent-kind documents in the stream are silently skipped.</summary>
    public ValueTask<IReadOnlyList<AgentGraphManifest>> LoadFromStringAsync(string content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var json = YamlAgentManifestLoader.YamlToJson(content);
        return _inner.LoadFromStringAsync(json, cancellationToken);
    }

    /// <summary>Parse graph manifests from a YAML file.</summary>
    public async ValueTask<IReadOnlyList<AgentGraphManifest>> LoadFromFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return await LoadFromStringAsync(content, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Parse any mix of <c>kind: Agent</c> + <c>kind: AgentGraph</c> documents from a YAML stream, preserving order.</summary>
    public ValueTask<IReadOnlyList<ManifestResource>> LoadAllResourcesFromStringAsync(string content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(content))
        {
            return ValueTask.FromResult<IReadOnlyList<ManifestResource>>(Array.Empty<ManifestResource>());
        }
        var json = YamlAgentManifestLoader.YamlToJson(content);
        return _inner.LoadAllResourcesFromStringAsync(json, cancellationToken);
    }
}
