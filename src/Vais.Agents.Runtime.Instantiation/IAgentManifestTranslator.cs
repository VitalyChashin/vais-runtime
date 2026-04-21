// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Vais.Agents.Control;
using Vais.Agents.Core;

namespace Vais.Agents.Runtime.Instantiation;

/// <summary>
/// Translates a stored <c>AgentManifest</c> into <see cref="StatefulAgentOptions"/>
/// ready to seed a <c>StatefulAiAgent</c>. The translator is the v0.17 Pillar B
/// seam — grain activation (<c>Func&lt;string, StatefulAgentOptions&gt;</c> from
/// <c>ConfigureAgentGrains</c>) calls <see cref="TranslateForGrain"/>; code paths
/// that want async + cancellation go through <see cref="TranslateAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Caching.</b> Implementations own an in-memory, per-id cache of translated
/// options. Callers invalidate via <c>InvalidateAsync</c> (inherited from
/// <see cref="IAgentManifestInvalidator"/>) after <c>UpdateAsync</c> /
/// <c>EvictAsync</c>. In-flight invocations keep their captured options
/// reference; the next activation picks up the new shape.
/// </para>
/// <para>
/// <b>Failures</b> surface as <see cref="ManifestInstantiationException"/> with
/// a structured <c>urn:vais-agents:*</c> URN. The HTTP surface maps that URN to
/// Problem Details unchanged.
/// </para>
/// </remarks>
public interface IAgentManifestTranslator : IAgentManifestInvalidator
{
    /// <summary>
    /// Load the manifest for <paramref name="agentId"/> from the registry and
    /// translate it. Returns a cached result on hit; registers the result in
    /// the cache on miss.
    /// </summary>
    /// <exception cref="ManifestInstantiationException">Translation failed — the URN describes why.</exception>
    /// <exception cref="ArgumentException">Thrown when the agent id is blank.</exception>
    ValueTask<StatefulAgentOptions> TranslateAsync(string agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronous entry point for the Orleans grain-activation seam
    /// (<c>ConfigureAgentGrains(sp, id =&gt; translator.TranslateForGrain(sp, id))</c>).
    /// Blocks on the underlying registry call — safe because the call is an
    /// in-process grain RPC (sub-millisecond in healthy clusters).
    /// </summary>
    /// <remarks>
    /// Prefer <see cref="TranslateAsync"/> where an async context is available;
    /// this method exists because <c>Func&lt;string, StatefulAgentOptions&gt;</c> is
    /// not async.
    /// </remarks>
    StatefulAgentOptions TranslateForGrain(IServiceProvider serviceProvider, string agentId);

    // NB: InvalidateAsync is declared on IAgentManifestInvalidator (inherited).
    // AgentLifecycleManager (Control.InProcess) depends on the parent interface
    // to stay layering-friendly; consumers calling TranslateAsync see the full
    // surface including invalidation.
}
