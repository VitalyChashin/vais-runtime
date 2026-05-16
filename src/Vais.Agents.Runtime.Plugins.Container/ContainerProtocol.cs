// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vais.Agents.Runtime.Plugins.Container;

internal sealed class PluginInvokeRequest
{
    public string AgentId { get; init; } = "";
    public string SessionId { get; init; } = "";
    public IReadOnlyList<PluginMessage> Messages { get; init; } = [];
    public string LlmGatewayUrl { get; init; } = "";
    public string ToolGatewayUrl { get; init; } = "";
    public JsonElement? OpaqueState { get; init; }
    public int TimeoutSeconds { get; init; } = 60;
    public PluginRequestContext Context { get; init; } = new();
}

internal sealed class PluginRequestContext
{
    public string? Traceparent { get; init; }
    public string? RunId { get; init; }
    public string? CorrelationId { get; init; }
    public string CallToken { get; init; } = "";
}

internal sealed class PluginMessage
{
    public string Role { get; init; } = "";
    public string? Content { get; init; }
    public IReadOnlyList<PluginToolCall>? ToolCalls { get; init; }
    public string? ToolCallId { get; init; }
}

internal sealed class PluginToolCall
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public JsonElement Arguments { get; init; }
}

internal sealed class PluginInvokeResponse
{
    public string AssistantMessage { get; init; } = "";
    public JsonElement? OpaqueState { get; init; }
    public IReadOnlyList<PluginJournalEntry>? Journal { get; init; }
    public PluginUsageCounts? Usage { get; init; }
}

internal sealed class PluginJournalEntry
{
    public string ToolName { get; init; } = "";
    public string ToolCallId { get; init; } = "";
    public string? InputJson { get; init; }
    public string? OutputJson { get; init; }
}

internal sealed class PluginUsageCounts
{
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int CachedTokens { get; init; }
}

internal sealed class PluginMetadataResponse
{
    public string HandlerTypeName { get; init; } = "";
    public string TargetApiVersion { get; init; } = "";
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public string? SdkVersion { get; init; }
}

internal sealed class PluginErrorResponse
{
    public string ErrorType { get; init; } = "InternalError";
    public string ErrorMessage { get; init; } = "";
    public string? DiagnosticTail { get; init; }
}

// Gateway callback request/response types (S18/S19)
//
// Contract v0.27 (discriminated union):
//   { messages: [...] }  → legacy pre-flattened path. Runtime treats Messages as the final
//                          CompletionRequest.History; no resolver/packer/telemetry runs.
//   { sections: [...] }  → canonical sectioned path. Runtime runs the full pipeline server-side
//                          (resolver → packer → telemetry emitter → flattener → LLM gateway),
//                          restoring per-section telemetry symmetry with runtime-hosted agents.
// Both populated, or both null/empty → HTTP 400 (urn:vais-agents:llm-complete-input-conflict).
internal sealed class GatewayLlmCompleteRequest
{
    public IReadOnlyList<PluginMessage>? Messages { get; init; }
    public IReadOnlyList<GatewaySection>? Sections { get; init; }
    public string? ModelId { get; init; }
    public GatewayLlmCompleteOptions? Options { get; init; }
}

internal sealed class GatewayLlmCompleteOptions
{
    public float? Temperature { get; init; }
    public int? MaxTokens { get; init; }
}

internal sealed class GatewayLlmCompleteResponse
{
    public PluginMessage Message { get; init; } = new();
    public PluginUsageCounts? Usage { get; init; }
}

internal sealed class GatewayToolInvokeRequest
{
    public string ToolName { get; init; } = "";
    public JsonElement Arguments { get; init; }
    public string ToolCallId { get; init; } = "";
}

internal sealed class GatewayToolInvokeResponse
{
    public string ToolCallId { get; init; } = "";
    public string Content { get; init; } = "";
    public bool IsError { get; init; }
}

internal sealed class GatewayToolInfo
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public System.Text.Json.JsonElement ParametersSchema { get; init; }
}

internal sealed class GatewayToolListResponse
{
    public IReadOnlyList<GatewayToolInfo> Tools { get; init; } = [];
}

// Section pipeline callback (contract v0.26 — see contracts/plugin-container/gateway-internal.md).
internal sealed class GatewaySectionsBuildRequest
{
    public IReadOnlyList<PluginMessage> Messages { get; init; } = [];
}

internal sealed class GatewaySectionsBuildResponse
{
    public IReadOnlyList<GatewaySection> Sections { get; init; } = [];
    public int TotalChars { get; init; }
}

internal sealed class GatewaySection
{
    public string Id { get; init; } = "";
    // Stringly typed on the wire so plugin SDKs in any language can dispatch on it without
    // depending on a generated enum mapping.
    public string Kind { get; init; } = "";
    public GatewaySectionPayload Payload { get; init; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Order { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProducerId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GatewaySectionBudget? Budget { get; init; }
}

internal sealed class GatewaySectionPayload
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Value { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PluginMessage? Turn { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<GatewayToolInfo>? Tools { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GatewayResponseFormatSpec? Spec { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Values { get; init; }
}

internal sealed class GatewaySectionBudget
{
    public int Priority { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxChars { get; init; }
}

internal sealed class GatewayResponseFormatSpec
{
    public JsonElement Schema { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    public bool Strict { get; init; }
}

// OpenAI-compat chat completions types for POST /v1/container-gateway/chat/completions
internal sealed class OpenAiChatMessage
{
    [JsonPropertyName("role")]         public string Role    { get; init; } = "";
    [JsonPropertyName("content")]      public string? Content { get; init; }
    // Assistant-side tool calls: model requested tool invocations.
    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<OpenAiToolCall>? ToolCalls { get; init; }
    // Tool-message correlation: ties the result back to the assistant tool_call.
    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; init; }
}

internal sealed class OpenAiToolCall
{
    [JsonPropertyName("id")]       public string             Id       { get; init; } = "";
    [JsonPropertyName("type")]     public string             Type     { get; init; } = "function";
    [JsonPropertyName("function")] public OpenAiToolFunction Function { get; init; } = new();
}

internal sealed class OpenAiToolFunction
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    // OpenAI sends arguments as a JSON STRING (not a nested object); the model's structured
    // emission is escaped into a string at protocol level. Deserialize on read.
    [JsonPropertyName("arguments")] public string Arguments { get; init; } = "";
}

internal sealed class OpenAiChatRequest
{
    [JsonPropertyName("model")]           public string Model       { get; init; } = "";
    [JsonPropertyName("messages")]        public IReadOnlyList<OpenAiChatMessage> Messages { get; init; } = [];
    [JsonPropertyName("temperature")]     public float? Temperature { get; init; }
    [JsonPropertyName("max_tokens")]      public int?   MaxTokens   { get; init; }
    [JsonPropertyName("stream")]          public bool?  Stream      { get; init; }
    [JsonPropertyName("response_format")] public OpenAiResponseFormat? ResponseFormat { get; init; }
}

internal sealed class OpenAiResponseFormat
{
    [JsonPropertyName("type")]        public string Type { get; init; } = "text";
    [JsonPropertyName("json_schema")] public OpenAiJsonSchema? JsonSchema { get; init; }
}

internal sealed class OpenAiJsonSchema
{
    [JsonPropertyName("name")]   public string      Name   { get; init; } = "";
    [JsonPropertyName("schema")] public System.Text.Json.JsonElement Schema { get; init; }
    [JsonPropertyName("strict")] public bool?       Strict { get; init; }
}

// ── Streaming chunk types (SSE response body) ─────────────────────────────
internal sealed class OpenAiChatChunk
{
    [JsonPropertyName("id")]      public string Id      { get; init; } = "";
    [JsonPropertyName("object")]  public string Object  { get; init; } = "chat.completion.chunk";
    [JsonPropertyName("created")] public long   Created { get; init; }
    [JsonPropertyName("model")]   public string Model   { get; init; } = "";
    [JsonPropertyName("choices")] public IReadOnlyList<OpenAiChatChunkChoice> Choices { get; init; } = [];
}

internal sealed class OpenAiChatChunkChoice
{
    [JsonPropertyName("index")]         public int            Index        { get; init; }
    [JsonPropertyName("delta")]         public OpenAiChatDelta Delta       { get; init; } = new();
    [JsonPropertyName("finish_reason")] public string?        FinishReason { get; init; }
}

internal sealed class OpenAiChatDelta
{
    [JsonPropertyName("role")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; init; }

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; init; }
}

internal sealed class OpenAiUsage
{
    [JsonPropertyName("prompt_tokens")]     public int PromptTokens     { get; init; }
    [JsonPropertyName("completion_tokens")] public int CompletionTokens { get; init; }
    [JsonPropertyName("total_tokens")]      public int TotalTokens      { get; init; }
}

internal sealed class OpenAiChatChoice
{
    [JsonPropertyName("index")]         public int             Index       { get; init; }
    [JsonPropertyName("message")]       public OpenAiChatMessage Message   { get; init; } = new();
    [JsonPropertyName("finish_reason")] public string          FinishReason { get; init; } = "stop";
}

internal sealed class OpenAiChatResponse
{
    [JsonPropertyName("id")]      public string Id      { get; init; } = "";
    [JsonPropertyName("object")]  public string Object  { get; init; } = "chat.completion";
    [JsonPropertyName("created")] public long   Created { get; init; }
    [JsonPropertyName("model")]   public string Model   { get; init; } = "";
    [JsonPropertyName("choices")] public IReadOnlyList<OpenAiChatChoice> Choices { get; init; } = [];
    [JsonPropertyName("usage")]   public OpenAiUsage? Usage { get; init; }
}
