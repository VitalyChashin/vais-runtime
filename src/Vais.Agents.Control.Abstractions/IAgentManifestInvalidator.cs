// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// Abstraction over a per-agent cache that the lifecycle manager evicts on
/// <c>UpdateAsync</c> / <c>EvictAsync</c>. The v0.17 manifest translator
/// (<c>Vais.Agents.Runtime.Instantiation.IAgentManifestTranslator</c>)
/// implements this contract so its options cache is cleared in lockstep with
/// registry updates — next grain activation re-translates against the current
/// manifest.
/// </summary>
/// <remarks>
/// Kept tiny + layering-friendly: lives in <c>Control.Abstractions</c> so
/// <c>AgentLifecycleManager</c> (in <c>Control.InProcess</c>) can depend on
/// it without flipping the layering against <c>Runtime.Instantiation</c>.
/// Host-side DI aliases the translator to this interface.
/// </remarks>
public interface IAgentManifestInvalidator
{
    /// <summary>Drop cached state for <paramref name="agentId"/>. Returns <c>true</c> if an entry was removed.</summary>
    ValueTask<bool> InvalidateAsync(string agentId, CancellationToken cancellationToken = default);
}
