// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json.Serialization;

namespace Vais.Agents.Control.Http;

/// <summary>Runtime origin of a loaded plugin.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PluginKind
{
    /// <summary>.NET assembly loaded via <c>AssemblyPluginLoader</c>.</summary>
    Assembly = 0,

    /// <summary>Python subprocess wired via MCP stdio.</summary>
    Python = 1,

    /// <summary>Container image served over the IP-1 HTTP protocol.</summary>
    Container = 2,
}

/// <summary>Current lifecycle state of a loaded plugin.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PluginState
{
    /// <summary>The plugin is being initialised (subprocess handshake or assembly load in progress).</summary>
    Loading = 0,

    /// <summary>The plugin is loaded and ready to accept handler invocations.</summary>
    Ready = 1,

    /// <summary>The plugin subprocess exited unexpectedly; a restart is being attempted.</summary>
    Restarting = 2,

    /// <summary>The plugin failed to load or all restart attempts were exhausted; it cannot serve requests.</summary>
    Unavailable = 3,
}

/// <summary>
/// Serializable snapshot of a single loaded plugin. Returned by <c>GET /v1/plugins</c>.
/// </summary>
/// <param name="Name">Friendly plugin name — matches the folder name under the plugins directory.</param>
/// <param name="AssemblyPath">
/// Absolute path to the primary plugin assembly on the host file-system (assembly plugins);
/// empty string for Python plugins (use <see cref="PluginKind"/> to distinguish).
/// </param>
/// <param name="TargetApiVersion">Abstractions major version the plugin was compiled or declared against.</param>
/// <param name="Handlers"><c>AgentManifest.Handler.TypeName</c> values this plugin advertises.</param>
/// <param name="LoadedViaAttribute"><c>true</c> when the plugin declared <c>[VaisPlugin]</c>; <c>false</c> for convention-discovered or Python plugins.</param>
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

    /// <summary>OS process ID of the subprocess (Python plugins); <see langword="null"/> for assembly plugins.</summary>
    public int? ProcessId { get; init; }

    /// <summary>
    /// MCP tool names declared by the plugin (Python plugins); <see langword="null"/> for assembly plugins.
    /// Populated from <c>plugin.yaml</c> <c>[tool.vais.plugin].tools</c>.
    /// </summary>
    public IReadOnlyList<string>? ToolNames { get; init; }

    /// <summary>
    /// Last few stderr lines from the most recent subprocess spawn (Python plugins);
    /// <see langword="null"/> for assembly plugins or when no output was captured.
    /// </summary>
    public string? LastErrorSnippet { get; init; }

    /// <summary>
    /// Container image reference (container plugins); <see langword="null"/> for assembly and Python plugins.
    /// </summary>
    public string? Image { get; init; }

    /// <summary>
    /// Deployment topology of a container plugin: "standalone", "sidecar", or "kubernetes".
    /// <see langword="null"/> for assembly and Python plugins.
    /// </summary>
    public string? Topology { get; init; }

    /// <summary>
    /// Kubernetes Deployment name (kubernetes topology only); <see langword="null"/> otherwise.
    /// </summary>
    public string? KubernetesDeploymentName { get; init; }

    /// <summary>
    /// Kubernetes namespace (kubernetes topology only); <see langword="null"/> otherwise.
    /// </summary>
    public string? KubernetesNamespace { get; init; }
}

/// <summary>Response body for <c>GET /v1/plugins</c>.</summary>
public sealed record PluginListResponse(IReadOnlyList<PluginInfo> Items);

/// <summary>Outcome of a <c>POST /v1/plugins/{name}/source</c> source-push request.</summary>
public enum PluginSourcePushStatus
{
    /// <summary>Archive unpacked and new subprocess started; MCP handshake succeeded.</summary>
    Success = 0,
    /// <summary>New subprocess started but the MCP handshake failed. Plugin is now Unavailable.</summary>
    HandshakeFailed = 1,
    /// <summary>The new <c>plugin.yaml</c> carries a different handler type name. Silo restart required.</summary>
    HandlerTypeNameChanged = 2,
    /// <summary>No supervisor found for this plugin name. The plugin was not loaded at startup.</summary>
    NoSupervisor = 3,
    /// <summary>Hot-reload is disabled. Set <c>VAIS_PYTHON_PLUGINS_RELOAD_POLICY=DrainAndSwap</c> to enable.</summary>
    ReloadDisabled = 4,
    /// <summary>The tar.gz archive could not be extracted (corrupt archive or path traversal attempt).</summary>
    UnpackFailed = 5,
    /// <summary><c>plugin.yaml</c> could not be re-parsed after the unpack. Old subprocess unaffected.</summary>
    ScanFailed = 6,
}

/// <summary>Response body for <c>POST /v1/plugins/{name}/source</c>.</summary>
public sealed record PluginSourcePushResponse(
    string PluginName,
    PluginSourcePushStatus Status,
    int? ProcessId,
    string? ErrorMessage);

/// <summary>Outcome of a <c>POST /v1/plugins/{name}/image</c> image-push request.</summary>
public enum PluginImageUpdateStatus
{
    /// <summary>Container replaced and health check passed.</summary>
    Success = 0,
    /// <summary>Docker image could not be started.</summary>
    StartFailed = 1,
    /// <summary>Container started but health check timed out.</summary>
    HandshakeFailed = 2,
    /// <summary>The new image declares a different handler type name. Silo restart required.</summary>
    HandlerTypeNameChanged = 3,
    /// <summary>No supervisor found for this plugin name. The plugin was not loaded at startup.</summary>
    NoSupervisor = 4,
    /// <summary>Kubernetes deployment patched; rolling update started. Not an error.</summary>
    RolloutStarted = 5,
}

/// <summary>Request body for <c>POST /v1/plugins/{name}/image</c>.</summary>
public sealed record PluginImageUpdateRequest(string Image);

/// <summary>Response body for <c>POST /v1/plugins/{name}/image</c>.</summary>
public sealed record PluginImageUpdateResponse(
    string PluginName,
    PluginImageUpdateStatus Status,
    string? FailureUrn);
