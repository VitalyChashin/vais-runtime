// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Extensions;

/// <summary>Options for <see cref="ExtensionAssemblyLoader"/>.</summary>
public sealed class ExtensionLoaderOptions
{
    /// <summary>
    /// The <c>Vais.Agents.Abstractions</c> major version the runtime expects extensions to target.
    /// Extensions whose <see cref="VaisExtensionAttribute.TargetApiVersion"/> does not match fail to load.
    /// Default: <c>"0.30"</c>.
    /// </summary>
    public string RuntimeAbiVersion { get; init; } = "0.30";

    /// <summary>
    /// When true, the <see cref="DefaultExtensionReloader"/> monitors the old ALC via a
    /// <see cref="WeakReference"/> after unload and logs a warning if the context is still
    /// alive after 30 seconds. Default: false (add opt-in overhead only when debugging leaks).
    /// </summary>
    public bool DiagnoseUnloadLeaks { get; init; }
}
