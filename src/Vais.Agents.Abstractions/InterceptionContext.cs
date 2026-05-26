// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// The kind of ontology-bound operation an <see cref="OntologyInterceptor"/> is observing.
/// <list type="bullet">
///   <item><description><see cref="List"/> — discovery / catalog read (e.g. MCP <c>tools/list</c>, design <c>vais.list</c>).</description></item>
///   <item><description><see cref="Call"/> — single invocation of a concept (e.g. MCP <c>tools/call</c>, south tool dispatch).</description></item>
/// </list>
/// </summary>
public enum OntologyOperation
{
    /// <summary>Discovery / catalog read. The chain shapes a returned set of concept entries.</summary>
    List = 0,

    /// <summary>Single concept invocation. The chain wraps the dispatch and may inspect or mutate request and response.</summary>
    Call = 1,
}

/// <summary>
/// Transport-agnostic carrier passed through an <see cref="OntologyInterceptor"/> chain. Concrete
/// transports (south <c>ToolGatewayContext</c>, north MCP envelopes) subclass this with their
/// typed payload — the substrate only sees the cross-cutting fields.
/// </summary>
/// <remarks>
/// Holds only ambient context: the operation kind, the bound ontology (if any), and the agent
/// context. Payload access is the concrete subclass's responsibility — keep the substrate base
/// envelope-free so non-MCP tools are never routed through MCP-shaped state (see SEP-1763 §C1-0e).
/// </remarks>
public abstract class InterceptionContext
{
    /// <summary>The operation kind being intercepted.</summary>
    public required OntologyOperation Operation { get; init; }

    /// <summary>The agent context driving the operation (identity, scopes, workspace).</summary>
    public required AgentContext AgentContext { get; init; }

    /// <summary>The ontology bound at the interception site, or <c>null</c> if the chain runs unbound.</summary>
    public IOntologyBinding? Binding { get; init; }
}
