// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Serialization;
using Vais.Agents;

namespace Vais.Plugin.Sdk;

// Maps the wire shape {id, name, arguments} to/from ToolCallRequest(ToolName, Arguments, CallId).
internal sealed class WireToolCallRequestConverter : JsonConverter<ToolCallRequest>
{
    public override ToolCallRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? id = null, name = null;
        JsonElement arguments = default;

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected start of object for ToolCall.");

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var propName = reader.GetString()!;
            reader.Read();
            switch (propName)
            {
                case "id":
                    id = reader.GetString();
                    break;
                case "name":
                    name = reader.GetString();
                    break;
                case "arguments":
                    arguments = JsonSerializer.Deserialize<JsonElement>(ref reader, options);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        if (id is null) throw new JsonException("Missing 'id' field in ToolCall.");
        if (name is null) throw new JsonException("Missing 'name' field in ToolCall.");
        return new ToolCallRequest(name, arguments, id);
    }

    public override void Write(Utf8JsonWriter writer, ToolCallRequest value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("id", value.CallId);
        writer.WriteString("name", value.ToolName);
        writer.WritePropertyName("arguments");
        value.Arguments.WriteTo(writer);
        writer.WriteEndObject();
    }
}
