// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Vais.Agents.Runtime.Plugins.Python;

/// <summary>
/// Reads a <c>plugin.yaml</c> document into a <see cref="PluginYamlDocument"/> POCO.
/// Only parses the fields the runtime cares about; unknown keys are silently ignored.
/// </summary>
internal sealed class PluginYamlDeserializer
{
    private static readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Parses <paramref name="yaml"/> and returns the typed document, or
    /// <see langword="null"/> when the YAML is empty / null-document.
    /// Throws <see cref="YamlDotNet.Core.YamlException"/> on malformed input.
    /// </summary>
    internal PluginYamlDocument? Deserialize(string yaml) =>
        _deserializer.Deserialize<PluginYamlDocument?>(yaml);
}

/// <summary>Top-level shape of a <c>plugin.yaml</c> document.</summary>
internal sealed class PluginYamlDocument
{
    public string ApiVersion { get; set; } = "";
    public string Kind { get; set; } = "";
    public PluginYamlMetadata? Metadata { get; set; }
    public PluginYamlSpec? Spec { get; set; }
}

internal sealed class PluginYamlMetadata
{
    public string Name { get; set; } = "";
}

internal sealed class PluginYamlSpec
{
    public string Runtime { get; set; } = "";
    public string Entrypoint { get; set; } = "";
    public PluginYamlPythonSpec? Python { get; set; }
    public PluginYamlHealthSpec? Health { get; set; }
}

internal sealed class PluginYamlPythonSpec
{
    public string Version { get; set; } = "";
    public string Interpreter { get; set; } = "";
}

internal sealed class PluginYamlHealthSpec
{
    public int HandshakeTimeoutSeconds { get; set; } = 5;
    public string RestartPolicy { get; set; } = "never";
}
