// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;

namespace Vais.Plugin.Sdk;

/// <summary>
/// Base class for container plugin agents. Implement <see cref="InvokeAsync"/> for synchronous
/// response; optionally override <see cref="StreamAsync"/> for incremental token delivery.
/// </summary>
public abstract class ContainerPluginAgent
{
    /// <summary>
    /// Handles a plugin invocation. Called by the SDK for <c>POST /v1/invoke</c>.
    /// <see cref="InvokeRequest.Llm"/> and <see cref="InvokeRequest.Tools"/> are pre-configured
    /// gateway clients; use them for all LLM and tool calls.
    /// </summary>
    public abstract Task<InvokeResponse> InvokeAsync(
        InvokeRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Handles a streaming plugin invocation. Called by the SDK for <c>POST /v1/stream</c>.
    /// Default implementation calls <see cref="InvokeAsync"/> and emits a single <c>done</c> event.
    /// Override to yield incremental <c>delta</c> and tool lifecycle events.
    /// </summary>
    public virtual async IAsyncEnumerable<SseEvent> StreamAsync(
        InvokeRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await InvokeAsync(request, cancellationToken).ConfigureAwait(false);
        yield return new SseEvent("done", response);
    }
}

/// <summary>A single SSE event emitted from <see cref="ContainerPluginAgent.StreamAsync"/>.</summary>
/// <param name="Event">
/// Event type: <c>delta</c>, <c>tool.started</c>, <c>tool.completed</c>, <c>done</c>, or <c>error</c>.
/// </param>
/// <param name="Data">
/// Payload — serialised to JSON by the SDK. For <c>delta</c> use <see cref="DeltaPayload"/>;
/// for <c>done</c> use <see cref="InvokeResponse"/>.
/// </param>
public sealed record SseEvent(string Event, object Data);

/// <summary>Payload for a <c>delta</c> SSE event.</summary>
/// <param name="Text">Incremental text token from the LLM.</param>
public sealed record DeltaPayload(string Text);

/// <summary>Payload for a <c>tool.started</c> SSE event.</summary>
/// <param name="ToolName">Name of the tool being called.</param>
/// <param name="ToolCallId">Correlation ID for the tool call.</param>
public sealed record ToolStartedPayload(string ToolName, string ToolCallId);

/// <summary>Payload for a <c>tool.completed</c> SSE event.</summary>
/// <param name="ToolName">Name of the tool that completed.</param>
/// <param name="ToolCallId">Correlation ID matching <see cref="ToolStartedPayload.ToolCallId"/>.</param>
/// <param name="OutputJson">Serialised JSON result returned by the tool.</param>
public sealed record ToolCompletedPayload(string ToolName, string ToolCallId, string OutputJson);
