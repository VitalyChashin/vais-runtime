// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Plugins.Python;

/// <summary>
/// A Python subprocess agent invocation failed with a classified error type. Mirrors the HTTP
/// container path's <c>ContainerInvokeException</c>: the SDK encodes the semantic error type in the
/// JSON-RPC error message as <c>[vais.errorType=&lt;Type&gt;]</c>, the supervisor parses it, and this
/// exception carries it into <see cref="IClassifiedAgentError"/> so <c>GraphNodeRetry</c> and
/// <c>GraphFailed.ErrorType</c> treat stdio plugins the same as container plugins.
/// </summary>
internal sealed class PythonAgentInvokeException : Exception, IClassifiedAgentError
{
    public string ErrorType { get; }

    /// <summary>Transient (retryable) for the gateway/tool/timeout classes; terminal otherwise.</summary>
    public bool IsTransient => ErrorType is "LlmGatewayError" or "ToolError" or "Timeout";

    public PythonAgentInvokeException(string errorType, string message, Exception? inner = null)
        : base(message, inner)
    {
        ErrorType = errorType;
    }

    /// <summary>
    /// Extracts the error type the SDK encoded as <c>[vais.errorType=&lt;Type&gt;]</c> in the JSON-RPC
    /// error message, or <c>null</c> when the marker is absent (an unexpected/transport error).
    /// </summary>
    internal static string? TryParseErrorType(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        const string marker = "[vais.errorType=";
        var i = message.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return null;
        var start = i + marker.Length;
        var end = message.IndexOf(']', start);
        return end > start ? message[start..end] : null;
    }
}
