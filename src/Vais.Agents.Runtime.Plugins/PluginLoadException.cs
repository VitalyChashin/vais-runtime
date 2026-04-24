// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Plugins;

/// <summary>
/// Raised when the plugin loader encounters a fatal error while reading a
/// plugin directory — handler-name collisions, unreadable directories,
/// critical IO failure. Non-fatal per-plugin failures (bad DLL, ABI
/// mismatch, missing factory) are logged with the matching URN and the
/// loader continues with remaining plugins.
/// </summary>
public sealed class PluginLoadException : Exception
{
    /// <summary>URN identifying the failure class — one of <see cref="PluginUrns"/>.</summary>
    public string Urn { get; }

    /// <summary>Optional plugin directory / assembly path that triggered the failure.</summary>
    public string? PluginPath { get; }

    /// <summary>Construct with URN + message + optional plugin path.</summary>
    public PluginLoadException(string urn, string message, string? pluginPath)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(urn);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Urn = urn;
        PluginPath = pluginPath;
    }

    /// <summary>Construct with URN + message + inner exception + optional plugin path.</summary>
    public PluginLoadException(string urn, string message, Exception innerException, string? pluginPath)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(urn);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Urn = urn;
        PluginPath = pluginPath;
    }
}

/// <summary>
/// URNs emitted by the plugin loader. Non-fatal errors land in runtime logs;
/// fatal errors surface as <see cref="PluginLoadException"/> (startup crash).
/// The HTTP surface maps these unchanged into Problem Details.
/// </summary>
public static class PluginUrns
{
    /// <summary>Common prefix shared by every URN emitted from the plugin loader.</summary>
    public const string UrnPrefix = "urn:vais-agents:";

    /// <summary>Plugin DLL exists but the CLR refused to load it (missing dep, bad IL, incompatible runtime).</summary>
    public const string PluginLoadFailed = UrnPrefix + "plugin-load-failed";

    /// <summary><see cref="VaisPluginAttribute.TargetApiVersion"/> does not match the runtime's current ABI.</summary>
    public const string PluginAbiMismatch = UrnPrefix + "plugin-abi-mismatch";

    /// <summary>Two loaded plugins export the same <c>AgentHandlerRef.TypeName</c>.</summary>
    public const string PluginHandlerCollision = UrnPrefix + "plugin-handler-collision";

    /// <summary>Manifest references a <c>TypeName</c> no loaded plugin owns (and no declarative <c>Model</c> set).</summary>
    public const string PluginHandlerNotFound = UrnPrefix + "plugin-handler-not-found";

    /// <summary><c>IAgentHandlerFactory.CreateAsync</c> threw during activation.</summary>
    public const string PluginFactoryThrow = UrnPrefix + "plugin-factory-throw";
}

/// <summary>
/// URNs emitted during hot-reload operations (v0.22+). Complement
/// <see cref="PluginUrns"/> which covers startup-time failures.
/// </summary>
public static class PluginReloadUrns
{
    /// <summary>Plugin DLL could not be loaded during a hot-reload attempt; old descriptor is kept.</summary>
    public const string PluginReloadFailed = PluginUrns.UrnPrefix + "plugin-reload-failed";

    /// <summary>Reloaded plugin DLL declares an ABI version that does not match the runtime; old descriptor is kept.</summary>
    public const string PluginReloadAbiMismatch = PluginUrns.UrnPrefix + "plugin-reload-abi-mismatch";
}
