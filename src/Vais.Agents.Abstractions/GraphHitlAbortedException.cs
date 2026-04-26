// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Thrown when an <see cref="IHitlAgentGraph{TState}"/> handler returns
/// <see langword="null"/>, signalling that the in-progress graph run should be aborted.
/// A <see cref="GraphFailed"/> event is emitted before this exception propagates.
/// </summary>
public sealed class GraphHitlAbortedException : Exception
{
    /// <summary>Id of the <c>Interrupt</c>-kind node at which the handler returned null.</summary>
    public string NodeId { get; }

    /// <param name="nodeId">Id of the interrupt node whose handler returned null.</param>
    public GraphHitlAbortedException(string nodeId)
        : base($"HITL handler returned null for interrupt '{nodeId}' — run aborted.")
    {
        NodeId = nodeId;
    }
}
