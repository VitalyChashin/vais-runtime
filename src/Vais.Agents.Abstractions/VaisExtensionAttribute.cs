// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Marks an assembly as a Vais.Agents extension. The extension loader scans each
/// discovered DLL for this attribute; assemblies carrying it are loaded into a
/// collectible <c>ExtensionAssemblyLoadContext</c> and their handler types are
/// instantiated via DI and bound to the appropriate pipeline seams.
/// </summary>
/// <remarks>
/// Each type in <see cref="Handlers"/> must extend exactly one seam abstract class
/// (<see cref="AgentInputMiddleware"/>, <see cref="AgentOutputMiddleware"/>, etc.).
/// The loader inspects the type hierarchy at load time to determine which seam the
/// handler targets.
/// </remarks>
/// <example>
/// <code>
/// [assembly: VaisExtension(TargetApiVersion = "0.30",
///     Handlers = new[] { typeof(MyLogInput), typeof(MyLogOutput) })]
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class VaisExtensionAttribute : Attribute
{
    /// <summary>
    /// <c>Vais.Agents.Abstractions</c> major version the extension was built against (e.g. <c>"0.30"</c>).
    /// The loader refuses to load extensions whose major.minor does not match the runtime's ABI.
    /// </summary>
    public required string TargetApiVersion { get; init; }

    /// <summary>
    /// Handler types exported by this extension. Each must be a concrete, non-abstract class
    /// extending a supported seam abstract class (e.g. <see cref="AgentInputMiddleware"/>).
    /// </summary>
    public required Type[] Handlers { get; init; }
}
