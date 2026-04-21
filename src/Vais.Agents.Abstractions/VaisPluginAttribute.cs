// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Marks an assembly as a Vais.Agents plugin. The plugin loader scans each
/// discovered DLL for this attribute — assemblies carrying it are the
/// authoritative plugin descriptors; assemblies without fall through to a
/// convention-based scan for <see cref="IAgentHandlerFactory"/>
/// implementations.
/// </summary>
/// <remarks>
/// <para>
/// <b>ABI contract.</b> <see cref="TargetApiVersion"/> declares the
/// <c>Vais.Agents.Abstractions</c> major version the plugin was built
/// against — e.g. <c>"0.18"</c>. The loader compares this against the
/// runtime's current ABI; mismatches fail to load with
/// <c>urn:vais-agents:plugin-abi-mismatch</c>. Matching is major-only
/// during the 0.x pre-release phase (so <c>"0.18"</c> loads on any
/// 0.18.x runtime); once v1.0 lands, rules shift to
/// semver-major compatibility.
/// </para>
/// <para>
/// <b>Handlers list.</b> The <see cref="Handlers"/> strings enumerate the
/// <c>AgentManifest.Handler.TypeName</c> values this plugin advertises.
/// The loader registers each in the handler registry. Type names must be
/// globally unique across all loaded plugins — two plugins exporting the
/// same string fail runtime startup with
/// <c>urn:vais-agents:plugin-handler-collision</c>.
/// </para>
/// </remarks>
/// <example>
/// Typical usage in a plugin's <c>AssemblyInfo.cs</c>:
/// <code>
/// [assembly: VaisPlugin(
///     targetApiVersion: "0.18",
///     "MyApp.WeatherAgent", "MyApp.TicketingAgent")]
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class VaisPluginAttribute : Attribute
{
    /// <summary>Construct the attribute. <paramref name="handlers"/> is a params array so partners can list 1–N type names inline.</summary>
    /// <param name="targetApiVersion"><c>Vais.Agents.Abstractions</c> major version the plugin targets (e.g. <c>"0.18"</c>).</param>
    /// <param name="handlers">One or more fully-qualified <c>AgentManifest.Handler.TypeName</c> values this plugin exports.</param>
    /// <exception cref="ArgumentException"><paramref name="targetApiVersion"/> is empty or <paramref name="handlers"/> contains an empty entry.</exception>
    public VaisPluginAttribute(string targetApiVersion, params string[] handlers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetApiVersion);
        ArgumentNullException.ThrowIfNull(handlers);

        foreach (var handler in handlers)
        {
            if (string.IsNullOrWhiteSpace(handler))
            {
                throw new ArgumentException(
                    "VaisPlugin handler entries must be non-empty fully-qualified type names.",
                    nameof(handlers));
            }
        }

        TargetApiVersion = targetApiVersion;
        Handlers = handlers;
    }

    /// <summary><c>Vais.Agents.Abstractions</c> major version the plugin was built against.</summary>
    public string TargetApiVersion { get; }

    /// <summary><c>AgentManifest.Handler.TypeName</c> values this plugin advertises. Non-empty.</summary>
    public IReadOnlyList<string> Handlers { get; }
}
