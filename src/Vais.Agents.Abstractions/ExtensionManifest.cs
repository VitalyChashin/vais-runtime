// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Declarative manifest for an extension registered via <c>vais apply -f extension.yaml</c>.
/// Extensions attach middleware to pipeline seams (agentInput, agentOutput, toolGateway, …)
/// via either a C# in-process ALC (<c>host: csharp</c>) or a paired container (<c>host: container</c>).
/// </summary>
public sealed record ExtensionManifest(
    string Id,
    string Version,
    ExtensionSpec Spec,
    IReadOnlyDictionary<string, string>? Labels = null,
    string? Description = null);

/// <summary>Spec block of an extension manifest.</summary>
public sealed record ExtensionSpec
{
    /// <summary>Execution host: <c>csharp</c> (in-process ALC) or <c>container</c> (paired HTTP).</summary>
    public required string Host { get; init; }

    /// <summary>NuGet package identifier (informational, <c>host: csharp</c> only).</summary>
    public string? Package { get; init; }

    /// <summary>OCI image reference (<c>host: container</c>).</summary>
    public string? Image { get; init; }

    /// <summary>Container HTTP port (<c>host: container</c>).</summary>
    public int? Port { get; init; }

    /// <summary>Container topology: <c>standalone</c> | <c>sidecar</c> | <c>kubernetes</c>.</summary>
    public string? Topology { get; init; }

    /// <summary>Seconds to wait for container readiness (<c>host: container</c>).</summary>
    public int? StartupTimeoutSeconds { get; init; }

    /// <summary>Per-handler HTTP timeout in seconds (<c>host: container</c>). Default 5 s.</summary>
    public int? InvokeTimeoutSeconds { get; init; }

    /// <summary>Image pull policy for the container (<c>host: container</c>).</summary>
    public string? ImagePullPolicy { get; init; }

    /// <summary>Handler declarations. At least one handler is required.</summary>
    public IReadOnlyList<ExtensionHandler> Handlers { get; init; } = Array.Empty<ExtensionHandler>();

    /// <summary>Optional scope filter. Null = cluster-wide (matches every agent).</summary>
    public ExtensionScope? Scope { get; init; }

    /// <summary>Extension-level config key-value pairs. Passed to the DI container on load.</summary>
    public IReadOnlyDictionary<string, object?>? Config { get; init; }

    /// <summary>Secret references resolved at runtime and injected as env-vars.</summary>
    public IReadOnlyDictionary<string, string>? Secrets { get; init; }
}

/// <summary>A single handler within an extension spec.</summary>
public sealed record ExtensionHandler
{
    /// <summary>Stable identifier for this handler within the extension.</summary>
    public required string Id { get; init; }

    /// <summary>
    /// Target seam: <c>agentInput</c> | <c>agentOutput</c> | <c>toolGatewayMiddleware</c> | …
    /// </summary>
    public required string Seam { get; init; }

    /// <summary>Fully-qualified CLR type name implementing the seam abstract class (<c>host: csharp</c>).</summary>
    public string? TypeName { get; init; }

    /// <summary>Base path used by the runtime's HTTP proxy (<c>host: container</c>).</summary>
    public string? Endpoint { get; init; }

    /// <summary>
    /// Chain order: lower = outermost. Conflicts (two handlers same priority on the same seam in
    /// the same scope) fail apply with 409 listing both names.
    /// </summary>
    public int Priority { get; init; } = 100;

    /// <summary>
    /// Action on handler error: <c>fail</c> (propagate, default) or <c>skip</c> (log + continue).
    /// </summary>
    public string FailureMode { get; init; } = "fail";

    /// <summary>Per-handler timeout override (seconds). Null = use <see cref="ExtensionSpec.InvokeTimeoutSeconds"/>.</summary>
    public int? TimeoutSeconds { get; init; }
}

/// <summary>
/// Scope filter applied when building an agent's middleware chain.
/// Multiple fields AND together; values within each field OR together.
/// A null <see cref="ExtensionScope"/> means cluster-wide (matches every agent).
/// </summary>
public sealed record ExtensionScope(
    IReadOnlyList<string>? Workspaces = null,
    IReadOnlyList<string>? AgentIds = null,
    LabelSelector? Selector = null);

/// <summary>Simple label-selector for matching agents by their manifest labels.</summary>
public sealed record LabelSelector(IReadOnlyDictionary<string, string> MatchLabels);
