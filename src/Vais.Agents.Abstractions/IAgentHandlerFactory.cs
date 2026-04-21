// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Factory contract for code-authored agent handlers loaded via the v0.18
/// plugin model. Plugin authors implement this type and mark the assembly
/// with <see cref="VaisPluginAttribute"/>; the runtime's plugin loader
/// discovers factories, registers them by <see cref="HandlerTypeName"/>, and
/// invokes <see cref="CreateAsync"/> at grain activation time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle.</b> The runtime calls <see cref="CreateAsync"/> per grain
/// activation — that's on cold start, after idle eviction, and after each
/// <c>UpdateAsync</c>. Factories that wrap expensive-to-construct state
/// (SDK clients, loaded models, warm caches) should either memoise inside
/// the factory (static / instance fields with thread-safe lazy init) or
/// inject a DI-scoped singleton and resolve it per call.
/// </para>
/// <para>
/// <b>Auto-wrap.</b> Plugins that just want "load this <c>IAiAgent</c>
/// type by name, no per-manifest config" can skip implementing this
/// factory entirely — if the loader finds an <see cref="IAiAgent"/>
/// implementation and no matching <see cref="IAgentHandlerFactory"/>, it
/// synthesises a default factory that uses
/// <c>ActivatorUtilities.CreateInstance</c> against the plugin's type.
/// </para>
/// <para>
/// <b>DI surface.</b> The <c>serviceProvider</c> handed to
/// <see cref="CreateAsync"/> is the host's full DI container — plugins can
/// resolve any service the runtime host registered. The documented
/// contract surface includes <c>ISecretResolver</c>, <c>IHttpClientFactory</c>,
/// <c>ILogger&lt;T&gt;</c>, <c>IAgentRegistry</c>, <c>IAgentContextAccessor</c>,
/// <c>TimeProvider</c>, and every Vais.Agents service composed by
/// <c>CompositionRoot.ConfigureServices</c>.
/// </para>
/// </remarks>
public interface IAgentHandlerFactory
{
    /// <summary>
    /// Fully-qualified type name this factory produces. Matched
    /// case-sensitive (ordinal) against <c>AgentManifest.Handler.TypeName</c>.
    /// Must be unique across all loaded plugins — collision fails runtime
    /// startup with <c>urn:vais-agents:plugin-handler-collision</c>.
    /// </summary>
    string HandlerTypeName { get; }

    /// <summary>
    /// Construct the agent for the supplied manifest. Called once per grain
    /// activation; may be called multiple times in the same process for the
    /// same agent id after eviction / update.
    /// </summary>
    /// <param name="manifest">The applied manifest. Factory may inspect
    /// <c>Annotations</c> / <c>Labels</c> / <c>SystemPrompt</c> for per-agent
    /// customisation; ignoring the manifest is also valid for plugins that
    /// ship one-shape-fits-all agents.</param>
    /// <param name="serviceProvider">Runtime DI container — resolve any
    /// registered service.</param>
    /// <param name="cancellationToken">Activation-scope cancellation.</param>
    ValueTask<IAiAgent> CreateAsync(
        AgentManifest manifest,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default);
}
