// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Vais.Agents.Core;
using Vais.Agents.Runtime.Instantiation;

namespace Vais.Agents.Runtime.Plugins.Container.Tests;

/// <summary>
/// Shared test double for <see cref="IAgentManifestTranslator"/> used by container-gateway
/// endpoint tests that don't care about per-agent middleware shape. Returns <see cref="PerAgentChains"/>
/// populated from the DI-global <c>LlmGatewayMiddleware</c> / <c>ToolGatewayMiddleware</c> /
/// <c>AgentInputMiddleware</c> registrations — the same fallback the real translator applies
/// when an agent has no <c>LlmGatewayRef</c> / <c>McpGatewayRef</c>. Tests that DO care about
/// per-agent shape register their own dedicated fake.
/// </summary>
internal sealed class PermissiveFakeTranslator(IServiceProvider services) : IAgentManifestTranslator
{
    public ValueTask<StatefulAgentOptions> TranslateAsync(string agentId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new StatefulAgentOptions { AgentName = agentId });

    public ValueTask<StatefulAgentOptions> TranslateForGrain(IServiceProvider serviceProvider, string agentId, CancellationToken cancellationToken = default)
        => TranslateAsync(agentId, cancellationToken);

    public ValueTask<bool> InvalidateAsync(string agentId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(false);

    public ValueTask<PerAgentChains> ResolvePerAgentChainsAsync(string agentId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new PerAgentChains(
            services.GetServices<LlmGatewayMiddleware>().ToArray(),
            services.GetServices<ToolGatewayMiddleware>().ToArray(),
            services.GetServices<AgentInputMiddleware>().ToArray(),
            Budget: null));

    public ValueTask<IReadOnlyList<ITool>> ResolveAgentToolsAsync(string agentId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<ITool>>(Array.Empty<ITool>());
}
