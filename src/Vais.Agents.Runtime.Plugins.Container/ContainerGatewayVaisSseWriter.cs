// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace Vais.Agents.Runtime.Plugins.Container;

/// <summary>
/// Writes an <see cref="IAsyncEnumerable{CompletionUpdate}"/> as VAIS-native Server-Sent Events
/// to <see cref="HttpResponse.Body"/>. Used by the streaming form of
/// <c>POST /v1/container-gateway/llm/complete</c> (contract v0.27).
/// </summary>
/// <remarks>
/// <para>
/// The SSE wire shape is distinct from <see cref="ContainerGatewaySseWriter"/> (which emits
/// OpenAI <c>chat.completion.chunk</c> shape on <c>/chat/completions</c>). This writer emits:
/// </para>
/// <list type="bullet">
///   <item><description>one <c>event: delta</c> frame per non-empty text delta, with body
///     <c>{"textDelta":"...","modelId":"..."}</c> (modelId only on the first frame that carries it);</description></item>
///   <item><description>one terminal <c>event: done</c> frame with body
///     <c>{"usage":{"inputTokens":N,"outputTokens":M}}</c> (usage fields only when the provider
///     reported them).</description></item>
/// </list>
/// <para>
/// The shape matches the documented behaviour for the endpoint in
/// <c>contracts/plugin-container/gateway-internal.md</c> (v0.27) and the existing
/// <c>CompletionDelta</c> agent event in <c>Vais.Agents.Abstractions</c>.
/// </para>
/// </remarks>
internal static class ContainerGatewayVaisSseWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static async Task WriteAsync(
        HttpResponse response,
        IAsyncEnumerable<CompletionUpdate> updates,
        CancellationToken cancellationToken)
    {
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";

        string? modelId = null;
        int? promptTokens = null;
        int? completionTokens = null;

        await foreach (var update in updates.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            // Provider-reported metadata typically arrives on a single update (often the last);
            // capture whatever we see so the terminal frame can carry the totals.
            if (update.ModelId is not null) modelId = update.ModelId;
            if (update.PromptTokens is not null) promptTokens = update.PromptTokens;
            if (update.CompletionTokens is not null) completionTokens = update.CompletionTokens;

            if (update.TextDelta.Length == 0)
            {
                continue;
            }

            var frame = new VaisDeltaFrame
            {
                TextDelta = update.TextDelta,
                ModelId = modelId,
            };
            await WriteEventAsync(response, "delta", frame, cancellationToken).ConfigureAwait(false);
        }

        var done = new VaisDoneFrame
        {
            Usage = (promptTokens is not null || completionTokens is not null)
                ? new PluginUsageCounts
                {
                    InputTokens = promptTokens ?? 0,
                    OutputTokens = completionTokens ?? 0,
                }
                : null,
        };
        await WriteEventAsync(response, "done", done, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteEventAsync(
        HttpResponse response,
        string eventName,
        object body,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(body, body.GetType(), JsonOptions);
        await response.WriteAsync($"event: {eventName}\ndata: {json}\n\n", cancellationToken).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class VaisDeltaFrame
    {
        public string TextDelta { get; init; } = "";
        public string? ModelId { get; init; }
    }

    private sealed class VaisDoneFrame
    {
        public PluginUsageCounts? Usage { get; init; }
    }
}
