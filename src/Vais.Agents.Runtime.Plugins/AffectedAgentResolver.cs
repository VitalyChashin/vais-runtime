// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Plugins;

/// <summary>
/// Shared helper that computes the union of handler type names from the old and
/// new <see cref="PluginDescriptor"/> in a <see cref="PluginReloadResult"/>.
/// Used by <c>TranslatorInvalidationHook</c> and <c>GrainReactivationOnPluginReloadHook</c>
/// to enumerate agents affected by a reload.
/// </summary>
internal static class AffectedAgentResolver
{
    internal static HashSet<string> UnionHandlers(PluginReloadResult result)
    {
        var handlers = new HashSet<string>(StringComparer.Ordinal);
        if (result.OldDescriptor is not null)
            foreach (var h in result.OldDescriptor.Handlers) handlers.Add(h);
        if (result.NewDescriptor is not null)
            foreach (var h in result.NewDescriptor.Handlers) handlers.Add(h);
        return handlers;
    }
}
