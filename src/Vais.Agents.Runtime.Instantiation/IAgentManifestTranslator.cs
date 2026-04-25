// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Vais.Agents.Control;
using Vais.Agents.Core;

namespace Vais.Agents.Runtime.Instantiation;

/// <summary>
/// Translates a stored <c>AgentManifest</c> into <see cref="StatefulAgentOptions"/>
/// ready to seed a <c>StatefulAiAgent</c>. The translator is the v0.17 Pillar B
/// seam — grain activation (<c>Func&lt;string, CancellationToken, ValueTask&lt;StatefulAgentOptions&gt;&gt;</c>
/// from <c>ConfigureAgentGrains</c>) calls <see cref="TranslateForGrain"/>; callers
/// that want async + cancellation can also go directly through <see cref="TranslateAsync"/>.
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
    /// Async entry point for the Orleans grain-activation seam
    /// (<c>ConfigureAgentGrains(async (sp, id, ct) =&gt; await translator.TranslateForGrain(sp, id, ct))</c>).
    /// Delegates to <see cref="TranslateAsync"/> — no sync-over-async bridging.
    /// </summary>
    ValueTask<StatefulAgentOptions> TranslateForGrain(IServiceProvider serviceProvider, string agentId, CancellationToken cancellationToken = default);

    // NB: InvalidateAsync is declared on IAgentManifestInvalidator (inherited).
    // AgentLifecycleManager (Control.InProcess) depends on the parent interface
    // to stay layering-friendly; consumers calling TranslateAsync see the full
    // surface including invalidation.
}
