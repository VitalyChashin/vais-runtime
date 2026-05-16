// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// Union of the resource kinds a manifest loader can emit. Closed hierarchy —
/// consumers pattern-match on subtype; adding a new <c>kind</c> is an unshipped
/// addition to Abstractions.
/// </summary>
/// <remarks>
/// <para>
/// <b>v0.9 kinds:</b> <see cref="AgentCase"/> (<c>kind: Agent</c>) +
/// <see cref="AgentGraphCase"/> (<c>kind: AgentGraph</c>). Both ride the
/// <c>vais.agents/v1</c> apiVersion group.
/// </para>
/// <para>
/// <b>Why a union rather than <c>object</c>?</b> GitOps workflows frequently drop
/// a mixed YAML file (<c>---</c>-separated) containing agent + graph resources
/// into a registry. The loader needs to preserve order + kind when consumers
/// apply the batch; a union is the minimum-overhead shape that keeps the contract
/// honest.
/// </para>
/// </remarks>
public abstract record ManifestResource
{
    private ManifestResource() { }

    /// <summary><c>kind: Agent</c>.</summary>
    public sealed record AgentCase(AgentManifest Manifest) : ManifestResource;

    /// <summary><c>kind: AgentGraph</c>.</summary>
    public sealed record AgentGraphCase(AgentGraphManifest Graph) : ManifestResource;

    /// <summary><c>kind: LlmGatewayConfig</c>.</summary>
    public sealed record LlmGatewayConfigCase(LlmGatewayConfigManifest Config) : ManifestResource;

    /// <summary><c>kind: McpGatewayConfig</c>.</summary>
    public sealed record McpGatewayConfigCase(McpGatewayConfigManifest Config) : ManifestResource;

    /// <summary><c>kind: McpServer</c>.</summary>
    public sealed record McpServerCase(McpServerManifest Server) : ManifestResource;

    /// <summary><c>kind: ContainerPlugin</c>.</summary>
    public sealed record ContainerPluginCase(ContainerPluginManifest Manifest) : ManifestResource;

    /// <summary><c>kind: EvalSuite</c>.</summary>
    public sealed record EvalSuiteCase(EvalSuiteManifest Suite) : ManifestResource;
}
