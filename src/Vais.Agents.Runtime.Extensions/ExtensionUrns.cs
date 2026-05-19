// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Extensions;

/// <summary>URN constants for extension lifecycle failures.</summary>
public static class ExtensionUrns
{
    /// <summary>DLL could not be loaded (bad IL, missing deps, IO error).</summary>
    public const string ExtensionLoadFailed    = "urn:vais:extension/load-failed";
    /// <summary>Extension TargetApiVersion does not match the runtime ABI.</summary>
    public const string ExtensionAbiBismatch   = "urn:vais:extension/abi-mismatch";
    /// <summary>Extension id was not found in the registry.</summary>
    public const string ExtensionNotFound      = "urn:vais:extension/not-found";
    /// <summary>Two handlers share the same seam and priority.</summary>
    public const string ExtensionPriorityConflict = "urn:vais:extension/priority-collision";
    /// <summary>Extension reload failed after a previous version was already loaded.</summary>
    public const string ExtensionReloadFailed  = "urn:vais:extension/reload-failed";
}
