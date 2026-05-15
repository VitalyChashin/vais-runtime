// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace Vais.Agents.Runtime.Plugins.Container;

/// <summary>
/// Writes an <see cref="IAsyncEnumerable{CompletionUpdate}"/> as OpenAI-compatible
/// <c>chat.completion.chunk</c> Server-Sent Events to <see cref="HttpResponse.Body"/>.
/// Flushes after every event so plugin clients (e.g. <c>openai-python</c>'s
/// <c>chat.completions.stream</c>) receive tokens as they arrive.
/// </summary>
internal static class ContainerGatewaySseWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static async Task WriteAsync(
        HttpResponse response,
        string completionId,
        string model,
        IAsyncEnumerable<CompletionUpdate> updates,
        CancellationToken cancellationToken)
    {
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";

        await foreach (var update in updates.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (update.TextDelta.Length == 0) continue;

            var chunk = new OpenAiChatChunk
            {
                Id      = completionId,
                Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Model   = model,
                Choices =
                [
                    new OpenAiChatChunkChoice
                    {
                        Index = 0,
                        Delta = new OpenAiChatDelta { Content = update.TextDelta },
                    },
                ],
            };
            await WriteEventAsync(response, chunk, cancellationToken).ConfigureAwait(false);
        }

        var finalChunk = new OpenAiChatChunk
        {
            Id      = completionId,
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model   = model,
            Choices =
            [
                new OpenAiChatChunkChoice
                {
                    Index        = 0,
                    Delta        = new OpenAiChatDelta(),
                    FinishReason = "stop",
                },
            ],
        };
        await WriteEventAsync(response, finalChunk, cancellationToken).ConfigureAwait(false);

        await response.WriteAsync("data: [DONE]\n\n", cancellationToken).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteEventAsync(
        HttpResponse response,
        OpenAiChatChunk chunk,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(chunk, JsonOptions);
        await response.WriteAsync($"data: {json}\n\n", cancellationToken).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
