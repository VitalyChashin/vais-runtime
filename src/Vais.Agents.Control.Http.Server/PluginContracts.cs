// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Http;

/// <summary>Runtime origin of a loaded plugin.</summary>
public enum PluginKind
{
    /// <summary>.NET assembly loaded via <c>AssemblyPluginLoader</c>.</summary>
    Assembly = 0,

    /// <summary>Python subprocess wired via MCP stdio.</summary>
    Python = 1,
}

/// <summary>Current lifecycle state of a loaded plugin.</summary>
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
}

/// <summary>Response body for <c>GET /v1/plugins</c>.</summary>
public sealed record PluginListResponse(IReadOnlyList<PluginInfo> Items);
