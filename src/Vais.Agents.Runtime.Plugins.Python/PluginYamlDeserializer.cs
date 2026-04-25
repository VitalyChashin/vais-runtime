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

    /// <summary>
    /// Discriminator for the plugin kind. One of <c>mcp-tool-server</c> (default, v0.23)
    /// or <c>agent-handler</c> (v0.24). Absent means <c>mcp-tool-server</c>.
    /// </summary>
    public string Kind { get; set; } = "";

    public string Entrypoint { get; set; } = "";
    public PluginYamlPythonSpec? Python { get; set; }
    public PluginYamlHealthSpec? Health { get; set; }

    /// <summary>Present when <see cref="Kind"/> is <c>agent-handler</c>.</summary>
    public PluginYamlHandlerSpec? Handler { get; set; }

    /// <summary>
    /// Optional map of secret ref-names to secret URIs (e.g. <c>MY_KEY: secret://env/MY_KEY</c>).
    /// Resolved by the runtime host before subprocess spawn and injected as
    /// <c>VAIS_SECRET_&lt;REF&gt;</c> environment variables.
    /// </summary>
    public Dictionary<string, string> Secrets { get; set; } = new();
}

internal sealed class PluginYamlHandlerSpec
{
    /// <summary>
    /// The <c>AgentHandlerRef.TypeName</c> this plugin registers. Must be unique across
    /// all loaded plugins (both .NET and Python). Required when spec.kind == agent-handler.
    /// </summary>
    public string TypeName { get; set; } = "";
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

    /// <summary>Per-invoke timeout for agent-handler plugins. Default 60 s.</summary>
    public int InvokeTimeoutSeconds { get; set; } = 60;
}
