// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

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
public enum PluginKind
{
    /// <summary>.NET assembly plugin.</summary>
    Assembly = 0,
    /// <summary>Python subprocess plugin.</summary>
    Python = 1,
}

/// <summary>Client-side mirror of <c>PluginState</c> from the server package.</summary>
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
}

/// <summary>Client-side wire type for <c>GET /v1/plugins</c>.</summary>
public sealed record PluginListResponse(IReadOnlyList<PluginInfo> Items);

/// <summary>
/// Client-side wire type for <c>POST /v1/graphs/validate</c> (v0.38).
/// Returned for all syntactically-valid requests; inspect <see cref="Valid"/> to
/// decide exit behaviour.
/// </summary>
/// <param name="Valid"><c>true</c> when no errors were found.</param>
/// <param name="Errors">Human-readable error strings, one per violation. Empty when <see cref="Valid"/> is <c>true</c>.</param>
public sealed record GraphValidationResult(bool Valid, IReadOnlyList<string> Errors);
