// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Declarative manifest for an assembly (C#) or Python plugin registered via
/// <c>vais apply -f plugin.yaml</c>. Mirrors <see cref="ContainerPluginManifest"/>
/// in shape; the DLL or source archive is uploaded separately (or bundled in the
/// multipart apply request when <c>--dll</c> is passed).
/// </summary>
public sealed record PluginManifest(
    string Id,
    string Version,
    string? Description = null,
    IReadOnlyDictionary<string, string>? Labels = null)
{
    /// <summary>Language and handler metadata.</summary>
    public required PluginManifestSpec Spec { get; init; }
}

/// <summary>Language and handler declarations for a plugin manifest.</summary>
public sealed record PluginManifestSpec
{
    /// <summary>Plugin language: <c>csharp</c> or <c>python</c>.</summary>
    public required string Language { get; init; }

    /// <summary>
    /// Optional list of handler type names the DLL/package is expected to export.
    /// When present and non-empty, the server cross-checks these against the
    /// handlers discovered in the uploaded DLL and returns <c>ValidationFailed</c>
    /// if any declared handler is missing.
    /// </summary>
    public IReadOnlyList<PluginHandlerRef>? Handlers { get; init; }
}

/// <summary>Declared handler type reference inside a <see cref="PluginManifestSpec"/>.</summary>
public sealed record PluginHandlerRef(string TypeName, string? AssemblyName = null);
