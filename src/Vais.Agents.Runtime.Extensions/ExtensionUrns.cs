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
    /// <summary>The extension's host value is not recognized by this runtime version.</summary>
    public const string HostNotSupported        = "urn:vais:extension/host-not-supported";
    /// <summary>The container's GET /v1/handlers call failed or timed out.</summary>
    public const string HandlerDiscoveryFailed  = "urn:vais:extension/handler-discovery-failed";
    /// <summary>The container's advertised handler set does not match the manifest.</summary>
    public const string HandlerMismatch         = "urn:vais:extension/handler-mismatch";
    /// <summary>A container extension targets a hot seam without operator acknowledgment.</summary>
    public const string HotSeamNotAcknowledged  = "urn:vais:extension/hot-seam-not-acknowledged";
}
