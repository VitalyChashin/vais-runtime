// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Http;

/// <summary>
/// Serializable snapshot of a single loaded plugin. Mirrors <see cref="Vais.Agents.Runtime.Plugins.PluginDescriptor"/>
/// minus the <c>AssemblyLoadContext</c> field which is a runtime-only handle not suitable for serialisation.
/// </summary>
/// <param name="Name">Friendly plugin name — matches the folder name under the plugins directory.</param>
/// <param name="AssemblyPath">Absolute path to the primary plugin assembly on the host file-system.</param>
/// <param name="TargetApiVersion">Abstractions major version the plugin was compiled against.</param>
/// <param name="Handlers"><c>AgentManifest.Handler.TypeName</c> values this plugin advertises.</param>
/// <param name="LoadedViaAttribute"><c>true</c> when the plugin declared <c>[VaisPlugin]</c>; <c>false</c> when the loader used convention discovery.</param>
public sealed record PluginInfo(
    string Name,
    string AssemblyPath,
    string TargetApiVersion,
    IReadOnlyList<string> Handlers,
    bool LoadedViaAttribute);

/// <summary>Response body for <c>GET /v1/plugins</c>.</summary>
public sealed record PluginListResponse(IReadOnlyList<PluginInfo> Items);
