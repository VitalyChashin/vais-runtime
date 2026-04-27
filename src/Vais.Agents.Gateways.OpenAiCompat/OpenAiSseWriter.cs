// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Vais.Agents.Gateways.OpenAiCompat.Models;

namespace Vais.Agents.Gateways.OpenAiCompat;

/// <summary>
/// Writes an <see cref="IAsyncEnumerable{CompletionUpdate}"/> as OpenAI-compatible
/// Server-Sent Events to <see cref="HttpResponse.Body"/>. Flushes after every event.
/// </summary>
internal static class OpenAiSseWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    internal static async Task WriteStreamAsync(
        HttpResponse response,
        string completionId,
        string model,
        IAsyncEnumerable<CompletionUpdate> updates,
        CancellationToken cancellationToken)
    {
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";

        IReadOnlyList<ToolCallRequest>? lastToolCalls = null;

        await foreach (var update in updates.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (update.ToolCalls is { Count: > 0 })
                lastToolCalls = update.ToolCalls;

            // Only emit a chunk if there is content to send (text delta or tool calls)
            if (update.TextDelta.Length == 0 && update.ToolCalls is not { Count: > 0 })
                continue;

            var chunk = OpenAiTranslator.ToChunk(update, completionId, model, finishReason: null);
            await WriteEventAsync(response, chunk, cancellationToken).ConfigureAwait(false);
        }

        // Final chunk carrying finish_reason
        var finishReason = lastToolCalls is { Count: > 0 } ? "tool_calls" : "stop";
        var finalChunk = new ChatCompletionChunk
        {
            Id = completionId,
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = model,
            Choices =
            [
                new ChatCompletionChunkChoice
                {
                    Index = 0,
                    Delta = new ChatDelta(),
                    FinishReason = finishReason
                }
            ]
        };
        await WriteEventAsync(response, finalChunk, cancellationToken).ConfigureAwait(false);

        await response.WriteAsync("data: [DONE]\n\n", cancellationToken).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteEventAsync(
        HttpResponse response,
        ChatCompletionChunk chunk,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(chunk, JsonOptions);
        await response.WriteAsync($"data: {json}\n\n", cancellationToken).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
