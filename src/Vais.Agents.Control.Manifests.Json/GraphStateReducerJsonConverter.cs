// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Vais.Agents.Control.Manifests;

/// <summary>
/// System.Text.Json converter for the <see cref="GraphStateReducer"/> closed hierarchy,
/// mirroring the wire form <see cref="JsonAgentGraphManifestLoader"/> accepts
/// (<c>"lastWriteWins"</c> / <c>"firstWriteWins"</c> / <c>"append"</c> /
/// <c>{ handlerRef: { … } }</c>). Registered on <see cref="EnvelopeCodec"/>'s options.
/// </summary>
internal sealed class GraphStateReducerJsonConverter : JsonConverter<GraphStateReducer>
{
    public override GraphStateReducer? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        var node = JsonNode.Parse(ref reader);
        if (node is JsonValue v && v.TryGetValue<string>(out var s))
        {
            return s switch
            {
                "lastWriteWins" => new GraphStateReducer.LastWriteWins(),
                "firstWriteWins" => new GraphStateReducer.FirstWriteWins(),
                "append" => new GraphStateReducer.Append(),
                _ => throw new JsonException($"Unknown GraphStateReducer '{s}'."),
            };
        }
        if (node is JsonObject obj && obj["handlerRef"] is JsonObject hr)
        {
            var typeName = hr["typeName"]?.GetValue<string>() ?? throw new JsonException("handlerRef.typeName required.");
            var asm = hr["assemblyName"]?.GetValue<string>();
            return new GraphStateReducer.HandlerRef(new GraphHandlerRef(typeName, asm));
        }
        throw new JsonException("GraphStateReducer must be a string or an object with handlerRef.");
    }

    public override void Write(Utf8JsonWriter writer, GraphStateReducer value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case GraphStateReducer.LastWriteWins:
                writer.WriteStringValue("lastWriteWins");
                break;
            case GraphStateReducer.FirstWriteWins:
                writer.WriteStringValue("firstWriteWins");
                break;
            case GraphStateReducer.Append:
                writer.WriteStringValue("append");
                break;
            case GraphStateReducer.HandlerRef h:
                writer.WriteStartObject();
                writer.WriteStartObject("handlerRef");
                writer.WriteString("typeName", h.Handler.TypeName);
                if (h.Handler.AssemblyName is not null) writer.WriteString("assemblyName", h.Handler.AssemblyName);
                writer.WriteEndObject();
                writer.WriteEndObject();
                break;
            default:
                throw new NotSupportedException($"Unknown GraphStateReducer subtype '{value.GetType().Name}'.");
        }
    }
}
