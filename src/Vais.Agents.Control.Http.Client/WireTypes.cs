// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json.Serialization;

namespace Vais.Agents.Control.Http;

/// <summary>
/// Client-side wire type for <c>POST /v1/agents</c> and <c>PATCH /v1/agents/{id}</c>.
/// Mirrors the server's <c>AgentApplyResponse</c> without depending on the server package.
/// </summary>
public sealed record AgentApplyResponse(
    AgentHandle Handle,
    IReadOnlyList<ApplyDiagnostic> Warnings);

/// <summary>
/// Client-side wire type for a non-fatal diagnostic in an apply response.
/// Mirrors the server's <c>ApplyDiagnostic</c>.
/// </summary>
public sealed record ApplyDiagnostic(string Urn, string Detail);

/// <summary>
/// Client-side wire type for <c>GET /v1/agents/{id}</c>. Mirrors the server's
/// equivalent shape in <c>Vais.Agents.Control.Http.Server</c> — the client
/// package re-declares the shape instead of depending on the server package,
/// so HTTP consumers (UIs, CLIs, tests) don't pull ASP.NET Core at reference time.
/// </summary>
public sealed record AgentQueryResponse(
    AgentManifest Manifest,
    AgentHandle Handle,
    AgentStatus Status);

/// <summary>Client-side wire type for paged list responses.</summary>
public sealed record AgentListResponse(
    IReadOnlyList<AgentManifest> Items,
    string? NextCursor = null);

/// <summary>
/// Client-side wire type for <c>GET /v1/graphs/{id}</c>. Mirrors the server's
/// <c>AgentGraphQueryResponse</c> without depending on the server package.
/// </summary>
public sealed record AgentGraphQueryResponse(
    AgentGraphManifest Manifest,
    AgentGraphHandle Handle,
    AgentGraphStatus Status);

/// <summary>Client-side wire type for <c>GET /v1/graphs</c>.</summary>
public sealed record AgentGraphListResponse(
    IReadOnlyList<AgentGraphManifest> Items,
    string? NextCursor = null);

/// <summary>
/// Client-side wire type for <c>POST /v1/graphs</c> and <c>PATCH /v1/graphs/{id}</c>.
/// Mirrors the server's <c>AgentGraphApplyResponse</c> without depending on the server package.
/// </summary>
public sealed record AgentGraphApplyResponse(
    AgentGraphHandle Handle,
    IReadOnlyList<ApplyDiagnostic> Warnings);

/// <summary>Client-side wire type for a single entry in <c>GET /v1/runtimes</c>.</summary>
public sealed record RuntimeInfo(string Url, string IdentityMode);

/// <summary>Client-side wire type for <c>GET /v1/runtimes</c>.</summary>
public sealed record RuntimeListResponse(IReadOnlyList<RuntimeInfo> Items);

/// <summary>Client-side mirror of <c>PluginKind</c> from the server package.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PluginKind
{
    /// <summary>.NET assembly plugin.</summary>
    Assembly = 0,
    /// <summary>Python subprocess plugin.</summary>
    Python = 1,
    /// <summary>Container image plugin served over the IP-1 HTTP protocol.</summary>
    Container = 2,
}

/// <summary>Client-side mirror of <c>PluginState</c> from the server package.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PluginState
{
    /// <summary>Plugin is still initialising.</summary>
    Loading = 0,
    /// <summary>Plugin is ready to serve requests.</summary>
    Ready = 1,
    /// <summary>Plugin subprocess crashed; restart in progress.</summary>
    Restarting = 2,
    /// <summary>Plugin failed permanently and cannot serve requests.</summary>
    Unavailable = 3,
}

/// <summary>Client-side wire type for a single entry in <c>GET /v1/plugins</c>.</summary>
/// <param name="Name">Friendly plugin name.</param>
/// <param name="AssemblyPath">Absolute path to the primary DLL (assembly plugins); empty string for Python plugins.</param>
/// <param name="TargetApiVersion">Abstractions API version the plugin targets.</param>
/// <param name="Handlers">Handler type names this plugin advertises.</param>
/// <param name="LoadedViaAttribute"><c>true</c> when loaded via <c>[VaisPlugin]</c>.</param>
public sealed record PluginInfo(
    string Name,
    string AssemblyPath,
    string TargetApiVersion,
    IReadOnlyList<string> Handlers,
    bool LoadedViaAttribute)
{
    /// <summary>Runtime origin of this plugin.</summary>
    public PluginKind Kind { get; init; } = PluginKind.Assembly;
    /// <summary>Current lifecycle state.</summary>
    public PluginState State { get; init; } = PluginState.Ready;
    /// <summary>OS process ID (Python plugins); <see langword="null"/> for assembly plugins.</summary>
    public int? ProcessId { get; init; }
    /// <summary>MCP tool names (Python plugins); <see langword="null"/> for assembly plugins.</summary>
    public IReadOnlyList<string>? ToolNames { get; init; }
    /// <summary>Last stderr lines from the subprocess (Python plugins); <see langword="null"/> otherwise.</summary>
    public string? LastErrorSnippet { get; init; }
    /// <summary>Container image reference (container plugins); <see langword="null"/> otherwise.</summary>
    public string? Image { get; init; }
    /// <summary>Deployment topology: "standalone", "sidecar", or "kubernetes" (container plugins); <see langword="null"/> otherwise.</summary>
    public string? Topology { get; init; }
    /// <summary>Kubernetes Deployment name (kubernetes topology only); <see langword="null"/> otherwise.</summary>
    public string? KubernetesDeploymentName { get; init; }
    /// <summary>Kubernetes namespace (kubernetes topology only); <see langword="null"/> otherwise.</summary>
    public string? KubernetesNamespace { get; init; }
}

/// <summary>Client-side wire type for <c>GET /v1/plugins</c>.</summary>
public sealed record PluginListResponse(IReadOnlyList<PluginInfo> Items);

/// <summary>Client-side mirror of <c>PluginSourcePushStatus</c> from the server package.</summary>
public enum PluginSourcePushStatus
{
    /// <summary>Archive unpacked and new subprocess started; MCP handshake succeeded.</summary>
    Success = 0,
    /// <summary>New subprocess started but the MCP handshake failed.</summary>
    HandshakeFailed = 1,
    /// <summary>The new <c>plugin.yaml</c> carries a different handler type name. Silo restart required.</summary>
    HandlerTypeNameChanged = 2,
    /// <summary>No supervisor found for this plugin name.</summary>
    NoSupervisor = 3,
    /// <summary>Hot-reload is disabled on this runtime.</summary>
    ReloadDisabled = 4,
    /// <summary>The tar.gz archive could not be extracted.</summary>
    UnpackFailed = 5,
    /// <summary><c>plugin.yaml</c> could not be re-parsed after the unpack.</summary>
    ScanFailed = 6,
    /// <summary>First push: venv provisioned and new subprocess started. HTTP 201 Created.</summary>
    Bootstrapped = 7,
    /// <summary>First push: venv provisioning (<c>python3.11 -m venv</c> or <c>pip install</c>) failed.</summary>
    BootstrapFailed = 8,
}

/// <summary>Client-side wire type for <c>POST /v1/plugins/{name}/source</c>.</summary>
public sealed record PluginSourcePushResponse(
    string PluginName,
    PluginSourcePushStatus Status,
    int? ProcessId,
    string? ErrorMessage);

/// <summary>Client-side mirror of <c>PluginImageUpdateStatus</c> from the server package.</summary>
public enum PluginImageUpdateStatus
{
    /// <summary>Container replaced and health check passed.</summary>
    Success = 0,
    /// <summary>Container could not be started.</summary>
    StartFailed = 1,
    /// <summary>Container started but health check timed out.</summary>
    HandshakeFailed = 2,
    /// <summary>The new image declares a different handler type name. Silo restart required.</summary>
    HandlerTypeNameChanged = 3,
    /// <summary>No supervisor found for this plugin name.</summary>
    NoSupervisor = 4,
    /// <summary>Kubernetes deployment patched; rolling update started. Not an error.</summary>
    RolloutStarted = 5,
}

/// <summary>Client-side wire type for <c>POST /v1/plugins/{name}/image</c>.</summary>
public sealed record PluginImageUpdateRequest(string Image);

/// <summary>Client-side wire type for <c>POST /v1/plugins/{name}/image</c> response.</summary>
public sealed record PluginImageUpdateResponse(
    string PluginName,
    PluginImageUpdateStatus Status,
    string? FailureUrn);

/// <summary>
/// Client-side wire type for <c>POST /v1/graphs/validate</c> (v0.38).
/// Returned for all syntactically-valid requests; inspect <see cref="Valid"/> to
/// decide exit behaviour.
/// </summary>
/// <param name="Valid"><c>true</c> when no errors were found.</param>
/// <param name="Errors">Human-readable error strings, one per violation. Empty when <see cref="Valid"/> is <c>true</c>.</param>
public sealed record GraphValidationResult(bool Valid, IReadOnlyList<string> Errors);

/// <summary>Client-side wire type for a single pipeline run returned by run-history endpoints.</summary>
/// <param name="RunId">Unique identifier of the run.</param>
/// <param name="GraphId">Identifier of the graph that produced this run.</param>
/// <param name="Status">Lifecycle status: <c>running</c>, <c>completed</c>, <c>failed</c>, or <c>interrupted</c>.</param>
/// <param name="StartedAt">UTC timestamp when the run was created.</param>
/// <param name="EndedAt">UTC timestamp when the run ended; <see langword="null"/> while running.</param>
/// <param name="DurationMs">Wall-clock duration in milliseconds; <see langword="null"/> while running.</param>
/// <param name="SuperSteps">Number of super-steps executed.</param>
/// <param name="Error">Error message when <paramref name="Status"/> is <c>failed</c>; otherwise <see langword="null"/>.</param>
public sealed record PipelineRunDto(
    string RunId,
    string GraphId,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    long? DurationMs,
    int SuperSteps,
    string? Error);

/// <summary>Client-side wire type for a single node execution returned by run-history endpoints.</summary>
/// <param name="RunId">Identifier of the containing run.</param>
/// <param name="NodeId">Identifier of the node within the graph definition.</param>
/// <param name="NodeKind">Registered handler type name.</param>
/// <param name="AgentId">Agent that handled this node; <see langword="null"/> for non-agent nodes.</param>
/// <param name="Status">Lifecycle status: <c>running</c>, <c>completed</c>, <c>failed</c>, or <c>interrupted</c>.</param>
/// <param name="StartedAt">UTC timestamp when this node execution started.</param>
/// <param name="EndedAt">UTC timestamp when this node execution ended; <see langword="null"/> while running.</param>
/// <param name="DurationMs">Wall-clock duration in milliseconds; <see langword="null"/> while running.</param>
/// <param name="InputText">Prompt text passed to the agent; <see langword="null"/> when not recorded.</param>
/// <param name="OutputText">Response text from the agent; <see langword="null"/> when not recorded.</param>
/// <param name="InputTokens">Number of prompt tokens consumed.</param>
/// <param name="OutputTokens">Number of completion tokens generated.</param>
/// <param name="Error">Error detail when <paramref name="Status"/> is <c>failed</c>; otherwise <see langword="null"/>.</param>
/// <param name="EdgesTaken">Edge labels traversed out of this node; <see langword="null"/> when not recorded.</param>
public sealed record NodeExecutionDto(
    string RunId,
    string NodeId,
    string NodeKind,
    string? AgentId,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    long? DurationMs,
    string? InputText,
    string? OutputText,
    int InputTokens,
    int OutputTokens,
    string? Error,
    IReadOnlyList<string>? EdgesTaken);

/// <summary>Client-side wire type for <c>GET /v1/graphs/{id}/runs</c>.</summary>
/// <param name="Items">Pipeline runs matching the filter criteria, ordered newest-first.</param>
public sealed record RunListResponse(IReadOnlyList<PipelineRunDto> Items);

/// <summary>Client-side wire type for <c>GET /v1/diagnostics/spans</c>. Mirrors server's <c>DiagSpanListResponse</c>.</summary>
public sealed record DiagSpanListResponse(IReadOnlyList<Vais.Agents.Control.DiagSpanRecord> Items);

/// <summary>Client-side wire type for <c>GET /v1/diagnostics/filter-status</c>. Mirrors server's <c>FilterStatusResponse</c>.</summary>
public sealed record FilterStatusResponse(IReadOnlyList<Vais.Agents.Control.FilterCallEntry> Calls, long TotalCalls);
