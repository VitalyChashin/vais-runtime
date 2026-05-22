// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Plugin.Sdk;

/// <summary>
/// The LLM gateway middleware chain failed. The SDK responds with HTTP 502 (<c>LlmGatewayError</c>).
/// Auto-raised by the LLM gateway client on an upstream non-2xx; may also be thrown manually.
/// </summary>
public sealed class LlmGatewayException : Exception
{
    /// <inheritdoc />
    public LlmGatewayException(string message) : base(message) { }

    /// <inheritdoc />
    public LlmGatewayException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// A tool-layer failure surfaced to the runtime. The SDK responds with HTTP 503 (<c>ToolError</c>).
/// Auto-raised by the tool gateway client when a tool call cannot be dispatched (non-2xx from the tool
/// gateway); throw manually to give up on a tool that returned an error result.
/// </summary>
public sealed class ToolException : Exception
{
    /// <inheritdoc />
    public ToolException(string message) : base(message) { }

    /// <inheritdoc />
    public ToolException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// The invocation exceeded its <c>timeoutSeconds</c> budget. The SDK responds with HTTP 504
/// (<c>Timeout</c>). Auto-raised by the SDK when the budget elapses; may also be thrown manually.
/// Named to avoid colliding with <see cref="System.TimeoutException"/>.
/// </summary>
public sealed class PluginTimeoutException : Exception
{
    /// <inheritdoc />
    public PluginTimeoutException(string message) : base(message) { }

    /// <inheritdoc />
    public PluginTimeoutException(string message, Exception inner) : base(message, inner) { }
}
