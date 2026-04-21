// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;

namespace Vais.Agents;

/// <summary>
/// Thrown when a graph node's cross-runtime agent invocation fails.
/// Carries the remote runtime URL and HTTP status so callers can distinguish
/// transient failures (retryable) from definitive ones (non-retryable).
/// </summary>
public sealed class RemoteAgentInvocationException : Exception
{
    /// <summary>Absolute URL of the remote runtime that returned the failure.</summary>
    public string RuntimeUrl { get; }

    /// <summary>HTTP status code returned by the remote runtime.</summary>
    public HttpStatusCode Status { get; }

    /// <summary>
    /// <see langword="true"/> when the failure is likely transient and the caller
    /// may retry (503 Service Unavailable, 504 Gateway Timeout, 429 Too Many Requests).
    /// <see langword="false"/> for definitive failures (404, 4xx other than 429).
    /// </summary>
    public bool IsRetryable => Status is HttpStatusCode.ServiceUnavailable
                                       or HttpStatusCode.GatewayTimeout
                                       or HttpStatusCode.TooManyRequests;

    /// <summary>
    /// Initialises the exception with the failing runtime URL, HTTP status, optional detail message and optional inner exception.
    /// </summary>
    public RemoteAgentInvocationException(
        string runtimeUrl,
        HttpStatusCode status,
        string? detail = null,
        Exception? inner = null)
        : base(detail ?? $"Remote agent at '{runtimeUrl}' returned HTTP {(int)status}.", inner)
    {
        RuntimeUrl = runtimeUrl;
        Status = status;
    }
}
