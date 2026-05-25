// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using Vais.Agents.Runtime.Extensions.Container.Wire;
using Xunit;

namespace Vais.Agents.Runtime.Extensions.Conformance;

/// <summary>
/// Asserts that every C# wire DTO serializes to the canonical camelCase JSON fixture
/// committed in <c>contracts/extensions/wire-fixtures/</c>.
/// Both C# (CamelCase + WhenWritingNull) and Python (alias_generator=to_camel + exclude_none)
/// must produce byte-identical output for the same logical payload; the fixtures are the shared oracle.
/// </summary>
public sealed class WireFixtureTests
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static JsonElement JE(string json) => JsonDocument.Parse(json).RootElement;

    private static string FixturePath(string seam, string variant) =>
        Path.Combine(AppContext.BaseDirectory, "wire-fixtures", seam, $"{variant}.json");

    private static void Check(object dto, string seam, string variant)
    {
        var actual = JsonNode.Parse(JsonSerializer.Serialize(dto, Opts))!;
        var expected = JsonNode.Parse(File.ReadAllText(FixturePath(seam, variant)))!;
        JsonNode.DeepEquals(actual, expected).Should().BeTrue(
            $"{dto.GetType().Name} should match wire-fixtures/{seam}/{variant}.json;\n" +
            $"  actual:   {actual}\n  expected: {expected}");
    }

    // ── agentInput ────────────────────────────────────────────────────────────

    [Fact]
    public void AgentInput_PreRequest() => Check(
        new AgentInputPreRequest("call-1", new AgentInputContextWire("agent-1", "run-1", "node-1", "hello")),
        "agent-input", "pre-request");

    [Fact]
    public void AgentInput_PreResponse_Mutate() => Check(
        new HandlerPreResponse("mutate", "tok-1", new Dictionary<string, object?> { ["color"] = "blue" }),
        "agent-input", "pre-response-mutate");

    [Fact]
    public void AgentInput_PostRequest() => Check(
        new AgentInputPostRequest("call-1", "tok-1"),
        "agent-input", "post-request");

    [Fact]
    public void AgentInput_PostResponse() => Check(
        new HandlerPostResponse("next", null),
        "agent-input", "post-response");

    // ── agentOutput ───────────────────────────────────────────────────────────

    [Fact]
    public void AgentOutput_PreRequest() => Check(
        new AgentOutputPreRequest("call-1", new AgentOutputContextWire("agent-1", "run-1", "session-1", 120, 30)),
        "agent-output", "pre-request");

    [Fact]
    public void AgentOutput_PreResponse_Mutate() => Check(
        new HandlerPreResponse("mutate", "tok-1", new Dictionary<string, object?> { ["color"] = "blue" }),
        "agent-output", "pre-response-mutate");

    [Fact]
    public void AgentOutput_PostRequest() => Check(
        new AgentOutputPostRequest("call-1", "tok-1"),
        "agent-output", "post-request");

    [Fact]
    public void AgentOutput_PostResponse() => Check(
        new HandlerPostResponse("next", null),
        "agent-output", "post-response");

    // ── toolGateway ───────────────────────────────────────────────────────────

    [Fact]
    public void ToolGateway_PreRequest() => Check(
        new ToolGatewayPreRequest(
            "call-1",
            new ToolGatewayContextWire(
                "search", "tool-call-1",
                JE("{\"query\":\"hello\"}"),
                "agent-1", "run-1", "standard", null,
                ["search", "fetch"])),
        "tool-gateway", "pre-request");

    [Fact]
    public void ToolGateway_PreResponse_ShortCircuit() => Check(
        new ToolGatewayPreResponse("shortCircuit", null, "cached result", null),
        "tool-gateway", "pre-response-short-circuit");

    [Fact]
    public void ToolGateway_PostRequest() => Check(
        new ToolGatewayPostRequest("call-1", "tok-1", "search result", null),
        "tool-gateway", "post-request");

    [Fact]
    public void ToolGateway_PostResponse_Mutate() => Check(
        new ToolGatewayPostResponse("mutate", "redacted", null),
        "tool-gateway", "post-response-mutate");

    // ── llmGateway ────────────────────────────────────────────────────────────

    [Fact]
    public void LlmGateway_PreRequest() => Check(
        new LlmGatewayPreRequest(
            "call-1",
            new LlmRequestWire(
                [new LlmMessageWire("user", "hello", null, null)],
                "You are helpful.", 0.7, 256, null, null,
                "agent-1", "run-1")),
        "llm-gateway", "pre-request");

    [Fact]
    public void LlmGateway_PreResponse_Mutate() => Check(
        new LlmGatewayPreResponse("mutate", "tok-1", new LlmResponseWire("synthetic reply", 10, 5), null),
        "llm-gateway", "pre-response-mutate");

    [Fact]
    public void LlmGateway_PostRequest() => Check(
        new LlmGatewayPostRequest("call-1", "tok-1", new LlmResponseWire("model reply", 10, 5)),
        "llm-gateway", "post-request");

    [Fact]
    public void LlmGateway_PostResponse_Mutate() => Check(
        new LlmGatewayPostResponse("mutate", new LlmResponseWire("rewritten reply", 10, 5)),
        "llm-gateway", "post-response-mutate");

    // ── errorInterceptor ──────────────────────────────────────────────────────

    [Fact]
    public void ErrorInterceptor_Request() => Check(
        new ErrorInterceptorRequest(
            "call-1",
            new ErrorContextWire("agent-1", "run-1", "node-1", "InvalidOperation", "something failed")),
        "error-interceptor", "request");

    [Fact]
    public void ErrorInterceptor_Response() => Check(
        new ErrorInterceptorResponse("friendly error"),
        "error-interceptor", "response");

    // ── graphNode ─────────────────────────────────────────────────────────────

    [Fact]
    public void GraphNode_PreRequest() => Check(
        new GraphNodePreRequest(
            "call-1",
            new GraphNodeContextWire(
                "run-1", "node-1", "llm", "agent-1", 0,
                new Dictionary<string, JsonElement> { ["query"] = JE("\"hello\"") })),
        "graph-node", "pre-request");

    [Fact]
    public void GraphNode_PreResponse_ShortCircuit() => Check(
        new GraphNodePreResponse(
            "shortCircuit", "tok-1",
            new Dictionary<string, JsonElement> { ["result"] = JE("\"cached\"") }),
        "graph-node", "pre-response-short-circuit");

    [Fact]
    public void GraphNode_PostRequest() => Check(
        new GraphNodePostRequest(
            "call-1", "tok-1",
            new Dictionary<string, JsonElement> { ["result"] = JE("\"node output\"") }),
        "graph-node", "post-request");

    [Fact]
    public void GraphNode_PostResponse_Mutate() => Check(
        new GraphNodePostResponse(
            "mutate",
            new Dictionary<string, JsonElement> { ["result"] = JE("\"transformed\"") }),
        "graph-node", "post-response-mutate");

    // ── sessionLifecycle ──────────────────────────────────────────────────────

    [Fact]
    public void SessionLifecycle_Request() => Check(
        new SessionLifecycleRequest(
            "call-1",
            new SessionLifecycleContextWire(
                "agent-1", "session-1", "closing", 2,
                [new SessionTurnWire("user", "hi"), new SessionTurnWire("assistant", "hello")])),
        "session-lifecycle", "request");
}
