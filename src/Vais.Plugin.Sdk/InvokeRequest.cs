// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Serialization;
using Vais.Agents;

namespace Vais.Plugin.Sdk;

/// <summary>
/// Request sent by the runtime shim to <c>POST /v1/invoke</c> and <c>POST /v1/stream</c>.
/// All LLM and tool calls must go through <see cref="Llm"/> and <see cref="Tools"/> — raw HTTP to
/// the gateway URLs violates architectural principle P4.
/// </summary>
public sealed class InvokeRequest
{
    /// <summary>Stable agent identifier.</summary>
    public string AgentId { get; init; } = "";

    /// <summary>Session / conversation identifier.</summary>
    public string SessionId { get; init; } = "";

    /// <summary>
    /// Assembled message history including system prompt, assembled by the preprocessing pipeline.
    /// Pass this list directly to <see cref="Llm"/>.<see cref="ILlmGatewayClient.CompleteAsync"/>.
    /// </summary>
    public IReadOnlyList<ChatTurn> Messages { get; init; } = [];

    /// <summary>Base URL for LLM gateway callbacks. Do not call directly; use <see cref="Llm"/>.</summary>
    public string LlmGatewayUrl { get; init; } = "";

    /// <summary>Base URL for tool gateway callbacks. Do not call directly; use <see cref="Tools"/>.</summary>
    public string ToolGatewayUrl { get; init; } = "";

    /// <summary>Plugin-private state from the previous invocation. <c>null</c> on the first call or after a fresh-start.</summary>
    public JsonElement? OpaqueState { get; init; }

    /// <summary>Invocation budget in seconds. Respect this when planning nested LLM/tool calls.</summary>
    public int TimeoutSeconds { get; init; } = 60;

    /// <summary>Ambient context for the current invocation (run ID, correlation ID, call token).</summary>
    public RequestContext Context { get; init; } = new();

    /// <summary>
    /// Pre-configured LLM gateway client. Set by the SDK before dispatching to
    /// <see cref="ContainerPluginAgent.InvokeAsync"/>; never null at dispatch time.
    /// </summary>
    [JsonIgnore]
    public ILlmGatewayClient Llm { get; internal set; } = null!;

    /// <summary>
    /// Pre-configured tool gateway client. Set by the SDK before dispatching to
    /// <see cref="ContainerPluginAgent.InvokeAsync"/>; never null at dispatch time.
    /// </summary>
    [JsonIgnore]
    public IToolGatewayClient Tools { get; internal set; } = null!;

    /// <summary>
    /// Deserialises <see cref="OpaqueState"/> into <typeparamref name="T"/>.
    /// Returns <c>null</c> when <see cref="OpaqueState"/> is <c>null</c> (first call or fresh-start).
    /// </summary>
    /// <exception cref="OpaqueStateDeserializationException">
    /// The stored JSON does not match <typeparamref name="T"/>. The SDK catches this and returns HTTP 422,
    /// which causes the grain to retry with <c>opaqueState: null</c>.
    /// </exception>
    public T? GetOpaqueState<T>() where T : class
    {
        if (OpaqueState is null) return null;
        try { return OpaqueState.Value.Deserialize<T>(PluginJsonOptions.Default); }
        catch (JsonException ex) { throw new OpaqueStateDeserializationException(ex.Message, ex); }
    }
}

/// <summary>Ambient context for a plugin invocation.</summary>
public sealed record RequestContext
{
    /// <summary>W3C <c>traceparent</c> header value from the caller.</summary>
    public string? Traceparent { get; init; }

    /// <summary>Run identifier — correlates all events within one agent graph run.</summary>
    public string? RunId { get; init; }

    /// <summary>Caller-supplied correlation identifier (e.g. an external request ID).</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Per-invocation bearer token. Forwarded by the gateway clients on every callback.</summary>
    public string CallToken { get; init; } = "";
}

/// <summary>
/// Thrown by <see cref="InvokeRequest.GetOpaqueState{T}"/> when the stored JSON cannot be deserialised
/// into the requested type. The SDK intercepts this exception and responds with HTTP 422, which
/// causes the runtime grain to clear its stored state and retry with <c>opaqueState: null</c>.
/// </summary>
public sealed class OpaqueStateDeserializationException : Exception
{
    /// <inheritdoc />
    public OpaqueStateDeserializationException(string message, Exception inner)
        : base(message, inner) { }
}
