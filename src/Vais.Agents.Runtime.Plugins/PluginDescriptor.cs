// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.Loader;

namespace Vais.Agents.Runtime.Plugins;

/// <summary>
/// Loaded-plugin descriptor emitted by <see cref="AssemblyPluginLoader"/>.
/// One descriptor per successfully loaded plugin subfolder.
/// </summary>
/// <param name="Name">Friendly plugin name — folder name under the plugins directory.</param>
/// <param name="AssemblyPath">Absolute path to the primary plugin assembly (the <c>.dll</c> that carried the <see cref="VaisPluginAttribute"/> or whose factory was discovered by convention).</param>
/// <param name="TargetApiVersion"><c>Vais.Agents.Abstractions</c> major version declared by the plugin (or the runtime's current ABI if discovered by convention).</param>
/// <param name="Handlers"><c>AgentManifest.Handler.TypeName</c> values this plugin advertises.</param>
/// <param name="LoadedViaAttribute">True when <see cref="VaisPluginAttribute"/> was present; false when the loader fell through to convention discovery.</param>
/// <param name="LoadContext">Per-plugin <see cref="AssemblyLoadContext"/> the plugin was loaded into. Kept alive for the plugin's lifetime.</param>
public sealed record PluginDescriptor(
    string Name,
    string AssemblyPath,
    string TargetApiVersion,
    IReadOnlyList<string> Handlers,
    bool LoadedViaAttribute,
    AssemblyLoadContext LoadContext);
