// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Host;

/// <summary>
/// Co-hosted-runtime <see cref="IAgentContextAccessor"/> that prefers the HTTP/MCP ingress
/// principal — pushed onto the control plane's <c>AsyncLocalAgentContextAccessor</c> by the
/// principal-mapping middleware — when present, and otherwise reads the silo-side
/// <c>OrleansAgentContextAccessor</c> (Orleans <c>RequestContext</c>) for grain turns.
/// </summary>
/// <remarks>
/// The control plane and the co-hosted silo share one DI container, so a single
/// <see cref="IAgentContextAccessor"/> registration must serve two stores: the ingress AsyncLocal
/// (carries the authenticated user + RBAC scopes set on each HTTP/MCP request) and the grain
/// RequestContext (carries the caller identity propagated across grain calls). Without this
/// composite the Orleans accessor wins the slot — its RequestContext is empty on the ingress
/// thread, so every authenticated apply synthesizes an anonymous principal and RBAC denies it.
/// On an ingress request thread the AsyncLocal carries a non-empty <see cref="AgentContext.UserId"/>;
/// on a silo grain turn it is empty, so this composite falls through to the Orleans accessor.
/// </remarks>
internal sealed class IngressFirstAgentContextAccessor(
    IAgentContextAccessor ingress,
    IAgentContextAccessor silo) : IAgentContextAccessor
{
    /// <inheritdoc />
    public AgentContext Current
    {
        get
        {
            var ingressContext = ingress.Current;
            return ingressContext.UserId is { Length: > 0 }
                ? ingressContext
                : silo.Current;
        }
    }
}
