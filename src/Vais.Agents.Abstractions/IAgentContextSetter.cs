// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Write-side complement to <see cref="IAgentContextAccessor"/>. Implementations
/// push a new <see cref="AgentContext"/> onto the ambient scope and return a
/// disposable token that restores the previous value on disposal.
/// </summary>
/// <remarks>
/// The default implementation in <c>Vais.Agents.Core</c>
/// (<c>AsyncLocalAgentContextAccessor</c>) implements both this interface and
/// <see cref="IAgentContextAccessor"/> via the same <c>AsyncLocal&lt;T&gt;</c> slot.
/// Register that type as a singleton under both service keys so inbound gateway
/// endpoints can inject <see cref="IAgentContextSetter"/> while middleware
/// continues to read via <see cref="IAgentContextAccessor"/>.
/// </remarks>
public interface IAgentContextSetter
{
    /// <summary>
    /// Pushes <paramref name="context"/> as the current context for the calling
    /// async flow. Dispose the returned token to restore the previous value.
    /// </summary>
    IDisposable Push(AgentContext context);
}
