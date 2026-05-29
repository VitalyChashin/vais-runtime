// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Vais.Agents.ScriptRuntime.Host.Tests;

/// <summary>
/// SR-4 / SR-5 — the Jint executor: result marshaling, no-CLR isolation, cooperative limits,
/// output cap, and the <c>__callTool</c> gateway callback (success, bad-token, budget cap).
/// </summary>
public sealed class ScriptExecutorTests
{
    private static ScriptExecutor Executor(HttpMessageHandler? handler = null) =>
        new(new HttpClient(handler ?? new StubHandler((_, _) => Json(200, new { toolCallId = "x", content = "", isError = false }))),
            NullLogger<ScriptExecutor>.Instance);

    private static ScriptRunRequest Req(string script, string prelude = "", CodeModeLimits? limits = null) => new()
    {
        RunId = "run-1",
        AgentId = "agent-1",
        Prelude = prelude,
        Script = script,
        ToolGatewayUrl = "http://gateway.local/v1/container-gateway/tools/invoke",
        CallToken = "test-token",
        Limits = limits ?? new CodeModeLimits(),
    };

    [Fact]
    public void NumberResult_IsJsonStringified()
    {
        var r = Executor().Execute(Req("return 1 + 2;"), default);
        r.Error.Should().BeNull();
        r.Result.Should().Be("3");
    }

    [Fact]
    public void StringResult_IsReturnedRaw_NotQuoted()
    {
        var r = Executor().Execute(Req("return 'hello';"), default);
        r.Error.Should().BeNull();
        r.Result.Should().Be("hello");
    }

    [Fact]
    public void ObjectResult_IsJsonEncoded()
    {
        var r = Executor().Execute(Req("return { a: 1, b: [2, 3] };"), default);
        r.Error.Should().BeNull();
        r.Result.Should().Be("{\"a\":1,\"b\":[2,3]}");
    }

    [Fact]
    public void NoClrAccess_SystemAndImportNamespaceUndefined()
    {
        var r = Executor().Execute(Req("return typeof System === 'undefined' && typeof importNamespace === 'undefined';"), default);
        r.Error.Should().BeNull();
        r.Result.Should().Be("true");
    }

    [Fact]
    public void Console_IsCaptured()
    {
        var r = Executor().Execute(Req("console.log('a', 1); console.warn('b'); return 0;"), default);
        r.Console.Should().Equal("a 1", "b");
    }

    [Fact]
    public void InfiniteLoop_HitsTimeout()
    {
        // High statement cap so the wall-clock timeout — not the statement limit — is what trips.
        var r = Executor().Execute(
            Req("while (true) {}", limits: new CodeModeLimits { TimeoutMs = 200, MaxStatements = int.MaxValue }),
            default);
        r.Error.Should().NotBeNull();
        r.Error!.Type.Should().Be("Timeout");
    }

    [Fact]
    public void RunawayLoop_HitsStatementLimit()
    {
        var r = Executor().Execute(
            Req("var s = 0; for (var i = 0; i < 1000000000; i++) { s += i; } return s;",
                limits: new CodeModeLimits { MaxStatements = 10_000, TimeoutMs = 30_000 }),
            default);
        r.Error.Should().NotBeNull();
        r.Error!.Type.Should().Be("StatementLimit");
    }

    [Fact]
    public void ScriptThrow_IsClassifiedAsScriptError()
    {
        var r = Executor().Execute(Req("throw new Error('boom');"), default);
        r.Error.Should().NotBeNull();
        r.Error!.Type.Should().Be("ScriptError");
    }

    [Fact]
    public void OutputExceedingCap_IsTruncated()
    {
        var r = Executor().Execute(
            Req("return 'x'.repeat(10000);", limits: new CodeModeLimits { MaxOutputBytes = 100 }),
            default);
        r.Error.Should().BeNull();
        r.Result.Should().EndWith("…[truncated]");
        Encoding.UTF8.GetByteCount(r.Result!).Should().BeLessThanOrEqualTo(100 + Encoding.UTF8.GetByteCount("…[truncated]"));
    }

    [Fact]
    public void ToolCall_RoutesToGateway_WithAuthAndRunHeaders()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var handler = new StubHandler((req, body) =>
        {
            captured = req;
            capturedBody = body;
            return Json(200, new { toolCallId = "sc-1", content = "echoed", isError = false });
        });

        var r = Executor(handler).Execute(
            Req("return tools.echo({ n: 5 });",
                prelude: "var tools = { echo: function (x) { return __callTool('svc', 'echo', JSON.stringify(x)); } };"),
            default);

        r.Error.Should().BeNull();
        r.Result.Should().Be("echoed");
        r.ToolCallCount.Should().Be(1);
        captured!.Headers.Authorization!.ToString().Should().Be("Bearer test-token");
        captured.Headers.GetValues("X-Run-Id").Should().ContainSingle().Which.Should().Be("run-1");
        captured.Headers.GetValues("X-Agent-Id").Should().ContainSingle().Which.Should().Be("agent-1");
        using var doc = JsonDocument.Parse(capturedBody!);
        doc.RootElement.GetProperty("toolName").GetString().Should().Be("echo");
        doc.RootElement.GetProperty("arguments").GetProperty("n").GetInt32().Should().Be(5);
    }

    [Fact]
    public void ToolCall_GatewayUnauthorized_IsClassifiedAsToolError()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var r = Executor(handler).Execute(
            Req("return tools.echo({});",
                prelude: "var tools = { echo: function (x) { return __callTool('svc', 'echo', JSON.stringify(x)); } };"),
            default);

        r.Error.Should().NotBeNull();
        r.Error!.Type.Should().Be("ToolError");
    }

    [Fact]
    public void ToolCallBudget_IsEnforced()
    {
        var handler = new StubHandler((_, _) => Json(200, new { toolCallId = "x", content = "ok", isError = false }));
        var r = Executor(handler).Execute(
            Req("tools.echo(); tools.echo(); tools.echo(); return 'done';",
                prelude: "var tools = { echo: function () { return __callTool('svc', 'echo', '{}'); } };",
                limits: new CodeModeLimits { MaxToolCalls = 2 }),
            default);

        r.Error.Should().NotBeNull();
        r.Error!.Type.Should().Be("ToolCallLimit");
    }

    private static HttpResponseMessage Json(int status, object body) => new((HttpStatusCode)status)
    {
        Content = JsonContent.Create(body),
    };

    /// <summary>Synchronous-capable stub handler — ScriptExecutor calls HttpClient.Send (sync).</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult() ?? "";
            return respond(request, body);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));
    }
}
