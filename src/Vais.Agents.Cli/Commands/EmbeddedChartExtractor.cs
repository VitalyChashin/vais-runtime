// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Reflection;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// Extracts the embedded Helm chart to a temporary directory on disk so that
/// <c>helm upgrade --install</c> can be pointed at it.
/// </summary>
internal static class EmbeddedChartExtractor
{
    private static readonly Assembly _assembly = typeof(EmbeddedChartExtractor).Assembly;

    // Embedded resource names use dots as both namespace and path separators.
    // All chart files are under this prefix.
    // MSBuild converts hyphens to underscores in embedded resource manifest names.
    private const string ChartPrefix = "Vais.Agents.Cli.Charts.vais_plugin.";

    /// <summary>
    /// Extracts all embedded chart resources into a new temp directory and returns
    /// the path to that directory. The caller is responsible for deleting it when done.
    /// </summary>
    internal static string ExtractToTemp()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"vais-chart-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        foreach (var resourceName in _assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(ChartPrefix, StringComparison.Ordinal)) continue;

            var relative = ResourceNameToRelativePath(resourceName[ChartPrefix.Length..]);
            var targetPath = Path.Combine(tempRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            using var stream = _assembly.GetManifestResourceStream(resourceName)!;
            using var file = File.Create(targetPath);
            stream.CopyTo(file);
        }

        return tempRoot;
    }

    // "templates.deployment.yaml" → "templates/deployment.yaml"
    // "Chart.yaml"               → "Chart.yaml"
    // "values.yaml"              → "values.yaml"
    internal static string ResourceNameToRelativePath(string name)
    {
        var lastDot = name.LastIndexOf('.');
        if (lastDot < 0) return name;

        var ext = name[(lastDot + 1)..];
        var pathPart = name[..lastDot].Replace('.', Path.DirectorySeparatorChar);
        return pathPart + "." + ext;
    }
}
