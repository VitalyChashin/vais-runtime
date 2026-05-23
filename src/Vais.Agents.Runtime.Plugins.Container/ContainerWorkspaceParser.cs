// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Plugins.Container;

/// <summary>
/// Validates and normalises a <c>spec.workspace</c> declaration into a <see cref="ContainerWorkspaceConfig"/>.
/// Shared by every parse path (CLI-apply manifest and filesystem <c>plugin.yaml</c>) so they cannot drift.
/// </summary>
internal static class ContainerWorkspaceParser
{
    /// <summary>Parses a manifest workspace spec, or returns null when none is declared.</summary>
    internal static ContainerWorkspaceConfig? FromSpec(
        ContainerPluginWorkspaceSpec? spec, ContainerPluginResourceBounds bounds) =>
        spec is null ? null : Parse(spec.Path, spec.SizeMb, spec.Medium, spec.Persist, bounds);

    /// <summary>
    /// Validates raw workspace values and returns a clamped config. Throws <see cref="ArgumentException"/>
    /// on any violation (bad path, non-positive size, unknown medium, or persist+memory).
    /// </summary>
    internal static ContainerWorkspaceConfig Parse(
        string path, int sizeMb, string medium, bool persist, ContainerPluginResourceBounds bounds)
    {
        var normalized = (path ?? "").TrimEnd('/');
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/') || normalized.Length == 0 || normalized == "/tmp")
            throw new ArgumentException(
                $"workspace.path must be an absolute path other than '/' or '/tmp' (got '{path}').");

        if (sizeMb <= 0)
            throw new ArgumentException($"workspace.sizeMb must be greater than 0 (got {sizeMb}).");

        // medium is an open backend identifier; only disk and memory are implemented today. A future
        // centralized backend (e.g. s3-snapshot) is added here and in DockerContainerSupervisor.BuildHostConfig.
        var parsedMedium = (medium ?? "").Trim().ToLowerInvariant() switch
        {
            "disk" => WorkspaceMedium.Disk,
            "memory" => WorkspaceMedium.Memory,
            _ => throw new ArgumentException(
                $"unsupported workspace medium '{medium}' (supported: disk, memory)."),
        };

        if (persist && parsedMedium == WorkspaceMedium.Memory)
            throw new ArgumentException(
                "workspace.persist=true is invalid with medium=memory (a tmpfs cannot persist).");

        var clamped = (int)Math.Min((long)sizeMb, bounds.MaxWorkspaceSizeMb);
        return new ContainerWorkspaceConfig(normalized, clamped, parsedMedium, persist);
    }
}
