// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Registered MCP server — physical or virtual. Physical: wraps a single upstream
/// server (same fields as <see cref="McpServerRef"/> plus registry metadata). Virtual:
/// aggregates N upstream registered servers behind one logical name with optional
/// tool projection (IBM Context Forge "virtual server" concept).
/// </summary>
/// <remarks>
/// Agents reference registered servers by setting <c>McpServerRef.Transport = "registered"</c>
/// and <c>McpServerRef.Name</c> to this manifest's <see cref="Id"/>. The
/// <c>AgentManifestTranslator</c> expands the ref at grain activation.
/// </remarks>
public sealed record McpServerManifest(
    string Id,
    string Version,
    string? Description = null,
    IReadOnlyDictionary<string, string>? Labels = null)
{
    // ── Physical server fields (mirror McpServerRef) ──────────────────────────

    /// <summary>Transport: "streamableHttp" | "sse" | "stdio" | "containerStdio". Required for physical servers.</summary>
    /// <remarks>
    /// <c>containerStdio</c> wraps a stdio-only MCP server in a runtime-supervised container behind a thin
    /// streamableHttp bridge. The runtime owns the container lifecycle; the manifest carries the image (or
    /// build context) and the stdio command via <see cref="Container"/>. See plan
    /// <c>plans/mcp-stdio-native-impl-2026-05-17.md</c>.
    /// </remarks>
    public string? Transport { get; init; }

    /// <summary>Server URL for <c>streamableHttp</c> / <c>sse</c> transports.</summary>
    public string? Url { get; init; }

    /// <summary>Executable path for <c>stdio</c> transport.</summary>
    public string? Command { get; init; }

    /// <summary>Command-line arguments for <c>stdio</c> transport.</summary>
    public IReadOnlyList<string>? Args { get; init; }

    /// <summary>Environment variables passed to <c>stdio</c> child processes.</summary>
    public IReadOnlyDictionary<string, string>? Env { get; init; }

    /// <summary>Optional <c>secret://</c> URI for bearer / header auth on HTTP transports.</summary>
    public string? AuthRef { get; init; }

    /// <summary>Optional tool allowlist. Null = expose all tools the server lists.</summary>
    public IReadOnlyList<string>? Tools { get; init; }

    /// <summary>
    /// Container spec for <c>containerStdio</c> transport. Required when
    /// <see cref="Transport"/> is <c>containerStdio</c>; must be null for other transports.
    /// </summary>
    public ContainerMcpSpec? Container { get; init; }

    // ── Virtual server fields ─────────────────────────────────────────────────

    /// <summary>True = virtual aggregator. False (default) = physical server.</summary>
    public bool Virtual { get; init; }

    /// <summary>Upstream server refs for virtual mode. Each <c>Ref</c> is an <see cref="Id"/> in <see cref="IMcpServerRegistry"/>.</summary>
    public IReadOnlyList<McpServerSourceRef>? Sources { get; init; }

    /// <summary>
    /// Tool projection for virtual mode. Maps visible tool name → source server id.
    /// Null = expose all tools from all sources (de-duplicated by name, first source wins).
    /// </summary>
    public IReadOnlyList<McpServerToolProjection>? ToolProjection { get; init; }

    // ── Governance ────────────────────────────────────────────────────────────

    /// <summary>
    /// Optional <see cref="McpGatewayConfigManifest.Id"/> applied per tool dispatch through this server.
    /// Lower precedence than an agent-level <see cref="AgentManifest.McpGatewayRef"/>.
    /// </summary>
    public string? McpGatewayRef { get; init; }

    /// <summary>
    /// Optional name of a deployment-supplied domain-ontology artifact to bind to this server.
    /// When set, the south cartridge resolves the artifact through <c>IDomainOntologyArtifactRegistry</c>
    /// and shapes the server's tools/list + tool-call dispatch with its tags, descriptions, and
    /// cross-refs. Missing or unknown refs degrade gracefully (no cartridge applied — passthrough).
    /// Plan C1-7.
    /// </summary>
    public string? OntologyRef { get; init; }

    /// <summary>Free-form operator-visible metadata.</summary>
    public IReadOnlyDictionary<string, string>? Annotations { get; init; }
}

/// <summary>Reference to an upstream registered <see cref="McpServerManifest"/> within a virtual server's <c>Sources</c> list.</summary>
/// <param name="Ref">The <see cref="McpServerManifest.Id"/> of the upstream server.</param>
public sealed record McpServerSourceRef(string Ref);

/// <summary>Maps a visible tool name to the source server in a virtual server's projection.</summary>
/// <param name="Name">The tool name as exposed to the agent.</param>
/// <param name="From">The <see cref="McpServerManifest.Id"/> of the source server that provides this tool.</param>
/// <param name="SourceToolName">Optional override for the upstream tool name if it differs from <see cref="Name"/>.</param>
public sealed record McpServerToolProjection(string Name, string From, string? SourceToolName = null);

/// <summary>Stable identity reference to a registered <see cref="McpServerManifest"/>.</summary>
public sealed record McpServerHandle(string Id, string Version);

/// <summary>Runtime status snapshot returned by <c>IMcpServerLifecycleManager.QueryAsync</c>.</summary>
public sealed record McpServerStatus(McpServerHandle Handle, bool Virtual, DateTimeOffset RegisteredAt);

/// <summary>
/// Container spec for an MCP server published via <c>transport: containerStdio</c>.
/// The runtime supervises one container per server; the container is expected to expose
/// MCP over streamableHttp at <see cref="Path"/> on <see cref="Port"/>. See
/// <c>samples/mcp-stdio-template/</c> for the canonical bridge pattern.
/// </summary>
public sealed record ContainerMcpSpec
{
    /// <summary>Container image reference. Mutually exclusive with <see cref="Build"/>.</summary>
    public string? Image { get; init; }

    /// <summary>Build-on-apply spec. The CLI builds the image and tags it before the manifest is sent. Mutually exclusive with <see cref="Image"/> only when neither is null.</summary>
    public ContainerMcpBuildSpec? Build { get; init; }

    /// <summary>Bridge HTTP port the container exposes. Default 7000 (chosen to dodge common dev ports — runtime 8080, plugin 8090, langfuse 3000, openwebui 5000).</summary>
    public int Port { get; init; } = 7000;

    /// <summary>Bridge MCP URL path. Default <c>/mcp</c>.</summary>
    public string Path { get; init; } = "/mcp";

    /// <summary>Bridge health endpoint path. Default <c>/health</c>; the runtime polls until it returns 2xx.</summary>
    public string HealthPath { get; init; } = "/health";

    /// <summary>Optional override of the image's default CMD (i.e. the bridge entrypoint).</summary>
    public IReadOnlyList<string>? Command { get; init; }

    /// <summary>Optional args appended to <see cref="Command"/> (or image CMD).</summary>
    public IReadOnlyList<string>? Args { get; init; }

    /// <summary>Environment variables passed to the container. The bridge reads its stdio child command from <c>MCP_STDIO_CMD</c> by convention.</summary>
    public IReadOnlyDictionary<string, string>? Env { get; init; }

    /// <summary>Secret references injected as env vars at container start. Key = env-var name; value = <c>secret://</c> URI.</summary>
    public IReadOnlyDictionary<string, string>? Secrets { get; init; }

    /// <summary>Resource limits — memory / cpu / pids. Defaults apply when null.</summary>
    public ContainerMcpResources? Resources { get; init; }

    /// <summary>Seconds to wait for the bridge <see cref="HealthPath"/> to return 2xx during initial start. Default 30.</summary>
    public int StartupTimeoutSeconds { get; init; } = 30;

    /// <summary>Docker image pull policy: <c>Always</c> | <c>IfNotPresent</c> | <c>Never</c>. Default IfNotPresent.</summary>
    public string ImagePullPolicy { get; init; } = "IfNotPresent";

    /// <summary>Kubernetes-specific config (required when the runtime supervisor is the K8s one).</summary>
    public ContainerMcpKubernetesConfig? Kubernetes { get; init; }
}

/// <summary>Build-on-apply specification for a container MCP server.</summary>
public sealed record ContainerMcpBuildSpec
{
    /// <summary>Docker build context path. Relative paths resolve against the manifest file's directory at CLI time.</summary>
    public required string Context { get; init; }

    /// <summary>Dockerfile path relative to <see cref="Context"/>. Default <c>Dockerfile</c>.</summary>
    public string Dockerfile { get; init; } = "Dockerfile";

    /// <summary>Optional <c>--build-arg</c> key=value pairs.</summary>
    public IReadOnlyDictionary<string, string>? Args { get; init; }

    /// <summary>Run <c>docker push</c> after a successful build. Default false.</summary>
    public bool Push { get; init; }
}

/// <summary>Resource limits applied to a container MCP server. Format mirrors Kubernetes resource quantities.</summary>
public sealed record ContainerMcpResources
{
    /// <summary>Memory limit (e.g. <c>128Mi</c>, <c>1Gi</c>). Null = supervisor default.</summary>
    public string? Memory { get; init; }

    /// <summary>CPU limit in cores (e.g. <c>0.25</c>, <c>1</c>, <c>2</c>). Null = supervisor default.</summary>
    public string? Cpu { get; init; }

    /// <summary>Max process IDs the container may spawn. Null = supervisor default.</summary>
    public long? PidsLimit { get; init; }
}

/// <summary>Kubernetes deployment coordinates for a container MCP server.</summary>
public sealed record ContainerMcpKubernetesConfig
{
    /// <summary>URL of the K8s Service the runtime should probe (e.g. <c>http://my-mcp.default.svc.cluster.local:7000</c>).</summary>
    public required string ServiceUrl { get; init; }

    /// <summary>K8s Deployment name (used for image patching).</summary>
    public string DeploymentName { get; init; } = "";

    /// <summary>K8s namespace. Default <c>default</c>.</summary>
    public string Namespace { get; init; } = "default";
}
