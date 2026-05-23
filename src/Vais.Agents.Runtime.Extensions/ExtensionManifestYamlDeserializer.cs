// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Vais.Agents.Runtime.Extensions;

/// <summary>
/// Deserializes a <c>kind: Extension</c> YAML document into an <see cref="ExtensionManifest"/>.
/// Unknown keys are silently ignored (<c>IgnoreUnmatchedProperties</c>).
/// </summary>
public sealed class ExtensionManifestYamlDeserializer
{
    private static readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Parse <paramref name="yaml"/> and return an <see cref="ExtensionManifest"/>.
    /// Throws <see cref="YamlDotNet.Core.YamlException"/> on malformed input, and
    /// <see cref="InvalidOperationException"/> when required fields are missing.
    /// </summary>
    public ExtensionManifest Deserialize(string yaml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yaml);
        var doc = _deserializer.Deserialize<ExtensionYamlDocument?>(yaml)
            ?? throw new InvalidOperationException("Extension YAML document is empty.");

        // Extension manifests use metadata.name (K8s-like); accept metadata.id as a fallback
        // for parity with the JSON loader (JsonAgentGraphManifestLoader.ParseExtension).
        var id = !string.IsNullOrWhiteSpace(doc.Metadata?.Name) ? doc.Metadata!.Name : doc.Metadata?.Id;
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException("Extension YAML: metadata.name (or metadata.id) is required.");
        }
        if (string.IsNullOrWhiteSpace(doc.Spec?.Host))
        {
            throw new InvalidOperationException("Extension YAML: spec.host is required.");
        }

        var spec = doc.Spec!;
        var handlers = (spec.Handlers ?? [])
            .Select(h => new ExtensionHandler
            {
                Id = h.Id ?? throw new InvalidOperationException("Extension YAML: each handler must have an id."),
                Seam = h.Seam ?? throw new InvalidOperationException($"Extension YAML: handler '{h.Id}' is missing seam."),
                TypeName = h.TypeName,
                Endpoint = h.Endpoint,
                Priority = h.Priority ?? 100,
                FailureMode = h.FailureMode ?? "fail",
                TimeoutSeconds = h.TimeoutSeconds,
            })
            .ToList();

        ExtensionScope? scope = null;
        if (spec.Scope is not null)
        {
            LabelSelector? selector = null;
            if (spec.Scope.Selector is { Count: > 0 })
            {
                selector = new LabelSelector(spec.Scope.Selector);
            }

            scope = new ExtensionScope(
                Workspaces: spec.Scope.Workspaces?.Count > 0 ? spec.Scope.Workspaces : null,
                AgentIds: spec.Scope.AgentIds?.Count > 0 ? spec.Scope.AgentIds : null,
                Selector: selector);
        }

        return new ExtensionManifest(
            Id: id!,
            Version: doc.Metadata!.Version ?? "0.0.0",
            Spec: new ExtensionSpec
            {
                Host = spec.Host,
                Package = spec.Package,
                Image = spec.Image,
                Port = spec.Port,
                Topology = spec.Topology,
                StartupTimeoutSeconds = spec.StartupTimeoutSeconds,
                InvokeTimeoutSeconds = spec.InvokeTimeoutSeconds,
                ImagePullPolicy = spec.ImagePullPolicy,
                Handlers = handlers,
                Scope = scope,
                Secrets = spec.Secrets?.Count > 0 ? spec.Secrets : null,
            },
            Labels: doc.Metadata.Labels?.Count > 0 ? doc.Metadata.Labels : null,
            Description: doc.Metadata.Description);
    }
}

// ── Internal YAML document POCOs ──────────────────────────────────────────────

internal sealed class ExtensionYamlDocument
{
    public string ApiVersion { get; set; } = "";
    public string Kind { get; set; } = "";
    public ExtensionYamlMetadata? Metadata { get; set; }
    public ExtensionYamlSpec? Spec { get; set; }
}

internal sealed class ExtensionYamlMetadata
{
    public string Name { get; set; } = "";
    public string? Id { get; set; }
    public string? Version { get; set; }
    public string? Description { get; set; }
    public Dictionary<string, string>? Labels { get; set; }
}

internal sealed class ExtensionYamlSpec
{
    public string? Host { get; set; }
    public string? Package { get; set; }
    public string? Image { get; set; }
    public int? Port { get; set; }
    public string? Topology { get; set; }
    public int? StartupTimeoutSeconds { get; set; }
    public int? InvokeTimeoutSeconds { get; set; }
    public string? ImagePullPolicy { get; set; }
    public List<ExtensionYamlHandler>? Handlers { get; set; }
    public ExtensionYamlScope? Scope { get; set; }
    public Dictionary<string, string>? Secrets { get; set; }
}

internal sealed class ExtensionYamlHandler
{
    public string? Id { get; set; }
    public string? Seam { get; set; }
    public string? TypeName { get; set; }
    public string? Endpoint { get; set; }
    public int? Priority { get; set; }
    public string? FailureMode { get; set; }
    public int? TimeoutSeconds { get; set; }
}

internal sealed class ExtensionYamlScope
{
    public List<string>? Workspaces { get; set; }
    public List<string>? AgentIds { get; set; }
    public Dictionary<string, string>? Selector { get; set; }
}
