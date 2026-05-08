// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Serialization;
using Vais.Agents;

namespace Vais.Plugin.Sdk;

// Maps the wire shape {role, content, toolCalls, toolCallId} to/from ChatTurn.
// Wire uses `content` (nullable); ChatTurn.Text is non-nullable (null wire → "").
internal sealed class WireChatTurnConverter : JsonConverter<ChatTurn>
{
    private static AgentChatRole ParseRole(string role) => role switch
    {
        "system" => AgentChatRole.System,
        "user" => AgentChatRole.User,
        "assistant" => AgentChatRole.Assistant,
        "tool" => AgentChatRole.Tool,
        _ => throw new JsonException($"Unknown Message role: '{role}'"),
    };

    private static string SerialiseRole(AgentChatRole role) => role switch
    {
        AgentChatRole.System => "system",
        AgentChatRole.User => "user",
        AgentChatRole.Assistant => "assistant",
        AgentChatRole.Tool => "tool",
        _ => throw new JsonException($"Cannot serialise unknown role: {role}"),
    };

    public override ChatTurn Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? role = null;
        string? content = null;
        IReadOnlyList<ToolCallRequest>? toolCalls = null;
        string? toolCallId = null;

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected start of object for ChatTurn.");

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var propName = reader.GetString()!;
            reader.Read();
            switch (propName)
            {
                case "role":
                    role = reader.GetString();
                    break;
                case "content":
                    content = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                    break;
                case "toolCalls":
                    toolCalls = JsonSerializer.Deserialize<IReadOnlyList<ToolCallRequest>>(ref reader, options);
                    break;
                case "toolCallId":
                    toolCallId = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        if (role is null) throw new JsonException("Missing 'role' field in Message.");
        return new ChatTurn(ParseRole(role), content ?? string.Empty, toolCalls, toolCallId);
    }

    public override void Write(Utf8JsonWriter writer, ChatTurn value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("role", SerialiseRole(value.Role));
        writer.WritePropertyName("content");
        // Null on assistant-only tool-call turns; required string on all other roles.
        if (value.Role == AgentChatRole.Assistant && value.Text.Length == 0)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value.Text);
        if (value.ToolCalls is { Count: > 0 })
        {
            writer.WritePropertyName("toolCalls");
            JsonSerializer.Serialize(writer, value.ToolCalls, options);
        }
        if (value.ToolCallId is not null)
            writer.WriteString("toolCallId", value.ToolCallId);
        writer.WriteEndObject();
    }
}
