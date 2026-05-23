// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;

namespace Vais.Agents.Runtime.Plugins.Container;

internal sealed class ContainerInvokeException : Exception, IClassifiedAgentError
{
    public HttpStatusCode StatusCode { get; }
    public string ErrorType { get; }
    public string? DiagnosticTail { get; }

    /// <summary>
    /// Transient (retryable) only for the gateway/tool/timeout statuses. 500 (InternalError) and 422
    /// (OpaqueStateDeserializationError) are terminal — a plugin code bug or unusable state should fail
    /// the node rather than loop under a retry policy.
    /// </summary>
    public bool IsTransient =>
        StatusCode is HttpStatusCode.BadGateway          // 502 LlmGatewayError
            or HttpStatusCode.ServiceUnavailable          // 503 ToolError
            or HttpStatusCode.GatewayTimeout;             // 504 Timeout

    public ContainerInvokeException(
        HttpStatusCode statusCode,
        string errorType,
        string errorMessage,
        string? diagnosticTail)
        : base(BuildMessage(statusCode, errorType, errorMessage, diagnosticTail))
    {
        StatusCode = statusCode;
        ErrorType = errorType;
        DiagnosticTail = diagnosticTail;
    }

    private static string BuildMessage(
        HttpStatusCode statusCode,
        string errorType,
        string errorMessage,
        string? diagnosticTail)
    {
        var msg = $"Container invocation failed (HTTP {(int)statusCode}): [{errorType}] {errorMessage}";
        if (!string.IsNullOrEmpty(diagnosticTail))
            msg += $"\nDiagnosticTail: {diagnosticTail}";
        return msg;
    }
}

internal sealed class OpaqueStateDeserializationException : Exception
{
    public OpaqueStateDeserializationException(string agentId)
        : base($"[{ContainerPluginUrns.OpaqueStateDeserializationError}] Container agent '{agentId}' " +
               "returned OpaqueStateDeserializationError on both the original call and the fresh-start retry.")
    {
    }
}
