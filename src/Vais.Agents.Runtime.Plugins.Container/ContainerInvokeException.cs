// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;

namespace Vais.Agents.Runtime.Plugins.Container;

internal sealed class ContainerInvokeException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string ErrorType { get; }
    public string? DiagnosticTail { get; }

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
