// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Vais.Agents.Runtime.Plugins.Container;

internal sealed class ContainerPluginYamlDeserializer
{
    private static readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    internal ContainerPluginYamlDocument? Deserialize(string yaml) =>
        _deserializer.Deserialize<ContainerPluginYamlDocument?>(yaml);
}

internal sealed class ContainerPluginYamlDocument
{
    public string ApiVersion { get; set; } = "";
    public string Kind { get; set; } = "";
    public ContainerPluginYamlMetadata? Metadata { get; set; }
    public ContainerPluginYamlSpec? Spec { get; set; }
}

internal sealed class ContainerPluginYamlMetadata
{
    public string Name { get; set; } = "";
}

internal sealed class ContainerPluginYamlSpec
{
    public string Runtime { get; set; } = "";
    public string Image { get; set; } = "";
    public int Port { get; set; } = 8080;
    public string Topology { get; set; } = "standalone";
    public string Durability { get; set; } = "";
    public int StartupTimeoutSeconds { get; set; } = 30;
    public int InvokeTimeoutSeconds { get; set; } = 60;
    public ContainerPluginYamlRetryPolicy? RetryPolicy { get; set; }
    public ContainerPluginYamlKubernetesSpec? Kubernetes { get; set; }
    public Dictionary<string, string> Secrets { get; set; } = new();
    public ContainerPluginYamlResources? Resources { get; set; }
}

internal sealed class ContainerPluginYamlResources
{
    public string? Memory    { get; set; }
    public string? Cpu       { get; set; }
    public long?   PidsLimit { get; set; }
}

internal sealed class ContainerPluginYamlKubernetesSpec
{
    public string ServiceUrl { get; set; } = "";
    public string DeploymentName { get; set; } = "";
    public string Namespace { get; set; } = "default";
}

internal sealed class ContainerPluginYamlRetryPolicy
{
    public int MaxAttempts { get; set; } = 3;
    public int BackoffSeconds { get; set; } = 2;
    public List<string> RetryOn { get; set; } = new();
}
