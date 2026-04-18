// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Schema;

namespace Vais2.Agents.Core;

/// <summary>
/// Helpers for building <see cref="ITool"/> instances from strongly-typed handlers,
/// closing the "tool authoring is raw JsonElement" gap every other framework
/// surveyed avoids. The JSON schema is generated via
/// <see cref="JsonSchemaExporter.GetJsonSchemaAsNode(JsonSerializerOptions, Type, JsonSchemaExporterOptions?)"/>
/// and cached on the returned tool instance.
/// </summary>
public static class Tool
{
    private static readonly JsonElement _emptyObjectSchema = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement;

    /// <summary>
    /// Create an <see cref="ITool"/> that deserializes its JSON arguments into
    /// <typeparamref name="TInput"/>, invokes <paramref name="handler"/>, and
    /// serializes the result. Schema inferred from <typeparamref name="TInput"/>.
    /// </summary>
    /// <typeparam name="TInput">Handler input type. Must be STJ-deserializable.</typeparam>
    /// <typeparam name="TOutput">Handler output type. Serialized to JSON (or passed through when <c>string</c>).</typeparam>
    /// <param name="name">Tool name. Non-empty.</param>
    /// <param name="description">Human-readable description shown to the model.</param>
    /// <param name="handler">Async handler. Receives deserialized input + cancellation token.</param>
    /// <returns>An <see cref="ITool"/> wrapping the handler.</returns>
    public static ITool FromFunc<TInput, TOutput>(
        string name,
        string description,
        Func<TInput, CancellationToken, Task<TOutput>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(handler);

        var schema = BuildSchema(typeof(TInput));
        return new FuncTool<TInput, TOutput>(name, description, schema, handler);
    }

    /// <summary>
    /// Create a no-argument <see cref="ITool"/>. Schema is the empty object
    /// (<c>{"type":"object","properties":{}}</c>) — the model knows it should call
    /// with no params.
    /// </summary>
    public static ITool FromFunc<TOutput>(
        string name,
        string description,
        Func<CancellationToken, Task<TOutput>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(handler);

        return new FuncTool<object?, TOutput>(
            name,
            description,
            _emptyObjectSchema,
            (_, ct) => handler(ct));
    }

    private static JsonElement BuildSchema(Type inputType)
    {
        var node = JsonSchemaExporter.GetJsonSchemaAsNode(JsonSerializerOptions.Default, inputType);
        return JsonSerializer.SerializeToElement(node);
    }

    private sealed class FuncTool<TInput, TOutput> : ITool
    {
        private readonly string _name;
        private readonly string _description;
        private readonly JsonElement _schema;
        private readonly Func<TInput, CancellationToken, Task<TOutput>> _handler;

        public FuncTool(
            string name,
            string description,
            JsonElement schema,
            Func<TInput, CancellationToken, Task<TOutput>> handler)
        {
            _name = name;
            _description = description;
            _schema = schema;
            _handler = handler;
        }

        public string Name => _name;
        public string Description => _description;
        public JsonElement ParametersSchema => _schema;

        public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
        {
            TInput input;
            try
            {
                input = arguments.ValueKind switch
                {
                    JsonValueKind.Undefined or JsonValueKind.Null => default!,
                    _ => arguments.Deserialize<TInput>() ?? default!,
                };
            }
            catch (JsonException ex)
            {
                throw new ArgumentException(
                    $"Failed to deserialize tool arguments into {typeof(TInput).Name}: {ex.Message}",
                    nameof(arguments),
                    ex);
            }

            var result = await _handler(input, cancellationToken).ConfigureAwait(false);
            return result switch
            {
                null => string.Empty,
                string s => s,
                _ => JsonSerializer.Serialize(result),
            };
        }
    }
}
