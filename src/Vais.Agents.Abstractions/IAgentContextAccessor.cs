// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Supplies the <see cref="AgentContext"/> for the currently-executing agent turn.
/// Replaces VAIS's <c>RequestContext.Get("UserId"/"ProjectId"/"FlowId")</c> pattern
/// with a stack-neutral surface.
/// </summary>
/// <remarks>
/// <para>
/// The default implementation in the library (<c>AsyncLocalAgentContextAccessor</c>
/// in <c>Vais.Agents.Core</c>) uses <see cref="System.Threading.AsyncLocal{T}"/>.
/// Hosts with their own ambient scope (Orleans' <c>RequestContext</c>, ASP.NET's
/// <c>HttpContext</c>) provide their own implementation.
/// </para>
/// <para>
/// Accessors must be cheap to call — the core reads the context on every turn.
/// </para>
/// </remarks>
public interface IAgentContextAccessor
{
    /// <summary>
    /// The context for the current turn. Must return <see cref="AgentContext.Empty"/>
    /// rather than null when no context has been set.
    /// </summary>
    AgentContext Current { get; }
}
