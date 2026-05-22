// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Implemented by exceptions that carry a semantic error classification, letting the graph retry
/// layer and orchestrators reason about a failure without referencing the component that produced it.
/// Container plugin failures (<c>LlmGatewayError</c>, <c>ToolError</c>, <c>Timeout</c>,
/// <c>InternalError</c>) surface through this so <see cref="GraphFailed.ErrorType"/> keeps the plugin's
/// error type instead of the .NET exception type name, and <c>GraphNodeRetry</c> retries only
/// transient failures.
/// </summary>
public interface IClassifiedAgentError
{
    /// <summary>Semantic error type, e.g. <c>"Timeout"</c>, <c>"LlmGatewayError"</c>, <c>"InternalError"</c>.</summary>
    string ErrorType { get; }

    /// <summary>True when the failure is transient and eligible for retry under a node's retry policy.</summary>
    bool IsTransient { get; }
}
