// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Gateways.StructuredOutput;

/// <summary>
/// Gateway middleware that validates each LLM response as <typeparamref name="T"/> via
/// <see cref="System.Text.Json"/>. Throws <see cref="AgentGuardrailDeniedException"/> with
/// <see cref="GuardrailLayer.Output"/> when the response text cannot be deserialized.
/// </summary>
/// <remarks>
/// Operates on the non-streaming path only. On the streaming path the update stream is passed
/// through unchanged; validation runs after the stream drains via
/// <see cref="LlmGatewayMiddleware.OnStreamCompleteAsync"/>.
/// </remarks>
/// <typeparam name="T">The expected JSON-deserializable response type.</typeparam>
public sealed class LlmJsonOutputMiddleware<T> : LlmGatewayMiddleware
{
    private readonly JsonSerializerOptions? _options;

    /// <summary>
    /// Initializes a new instance with optional JSON serializer options.
    /// </summary>
    public LlmJsonOutputMiddleware(JsonSerializerOptions? options = null)
        => _options = options;

    /// <inheritdoc/>
    protected override async Task<CompletionResponse> InvokeAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next,
        CancellationToken cancellationToken)
    {
        var response = await next(request, cancellationToken).ConfigureAwait(false);
        Validate(response.Text);
        return response;
    }

    /// <inheritdoc/>
    protected override ValueTask OnStreamCompleteAsync(
        CompletionResponse final,
        CancellationToken cancellationToken = default)
    {
        Validate(final.Text);
        return ValueTask.CompletedTask;
    }

    private void Validate(string text)
    {
        try
        {
            JsonSerializer.Deserialize<T>(text, _options);
        }
        catch (JsonException ex)
        {
            throw new AgentGuardrailDeniedException(GuardrailLayer.Output,
                $"Response is not valid {typeof(T).Name} JSON: {ex.Message}");
        }
    }
}
