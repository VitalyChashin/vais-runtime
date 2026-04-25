// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Python.Tests;

/// <summary>
/// End-to-end wire tests using inline Python echo agents (no external packages — stdlib only).
///
/// Unlike <see cref="PythonAgentWireTests"/>, these tests require only a Python 3 interpreter
/// on PATH; no SDK or LangGraph installation is needed.
///
/// Opt-in: <c>set VAIS_RUN_PYTHON_PLUGIN_TESTS=1</c>, then:
/// <code>
/// dotnet test tests/Vais.Agents.Runtime.Plugins.Python.Tests --filter PythonEchoWireTests
/// </code>
///
/// The third test (<see cref="SlowInvoke_ExceedsTimeout_ThrowsTimeoutExceptionPromptly"/>) is
/// a load-bearing bisect for the 2-minute Orleans timeout bug: if the supervisor's
/// <c>CancelAfter</c>+MCP cancellation path works, the test completes in a few seconds; if not,
/// it hangs for the Python script's full 30-second sleep before returning (without a
/// <see cref="TimeoutException"/>), exposing the bug at the unit level rather than in Docker.
///
/// The fourth test (<see cref="SlowAsyncioInvoke_ExceedsTimeout_ThrowsTimeoutExceptionPromptly"/>)
/// is the follow-up bisect for hypothesis 2: does the supervisor's <c>CancelAfter</c> still fire
/// when the Python side uses <c>asyncio.run()</c> per message (the pattern used by
/// <c>vais_agent_sdk._runner.run()</c> and all real LangGraph-backed plugins)?
/// </summary>
[Collection("PythonAgentWire")]
public sealed class PythonEchoWireTests
{
    private static readonly bool Enabled =
        string.Equals(
            Environment.GetEnvironmentVariable("VAIS_RUN_PYTHON_PLUGIN_TESTS"),
            "1",
            StringComparison.Ordinal);

    private static string Python =>
        Environment.GetEnvironmentVariable("VAIS_PYTHON_INTERPRETER") is { Length: > 0 } p
            ? p
            : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "python" : "python3";

    // Minimal stdlib-only MCP stdio server.
    // Pass {"delay": "N"} in the invoke context dict to sleep N seconds before replying —
    // used by the timeout bisect test.
    private const string EchoAgentPy = """
        import json, sys, time

        def send(obj):
            sys.stdout.write(json.dumps(obj) + "\n")
            sys.stdout.flush()

        for raw in sys.stdin:
            line = raw.strip()
            if not line:
                continue
            try:
                msg = json.loads(line)
            except Exception:
                continue
            method = msg.get("method", "")
            id_    = msg.get("id")
            params = msg.get("params") or {}

            if method == "initialize":
                send({"jsonrpc": "2.0", "id": id_, "result": {
                    "protocolVersion": "2024-11-05",
                    "capabilities": {"tools": {}},
                    "serverInfo": {"name": "echo-agent", "version": "0.1"}
                }})
            elif method == "tools/list":
                send({"jsonrpc": "2.0", "id": id_, "result": {"tools": []}})
            elif method == "ping" and id_ is not None:
                send({"jsonrpc": "2.0", "id": id_, "result": {}})
            elif method == "initialized":
                pass
            elif method == "vais/agent.invoke":
                ctx   = params.get("context") or {}
                delay = float(ctx.get("delay", "0"))
                if delay > 0:
                    time.sleep(delay)
                user_msg = params.get("userMessage", "")
                state_in = params.get("state")
                turn = 1
                if state_in:
                    try:    turn = json.loads(state_in).get("turn", 0) + 1
                    except: pass
                send({"jsonrpc": "2.0", "id": id_, "result": {
                    "assistantMessage": f"echo: {user_msg}",
                    "newState": json.dumps({"turn": turn})
                }})
            elif id_ is not None:
                send({"jsonrpc": "2.0", "id": id_, "error": {
                    "code": -32601, "message": f"Not found: {method}"
                }})
        """;

    // Asyncio-based MCP stdio server — matches the vais_agent_sdk._runner.run() dispatch
    // pattern used by every real Python plugin (asyncio.run() per message, asyncio.sleep
    // for the delay). This is the structural difference from EchoAgentPy: while handling
    // a request the stdin read-loop is blocked inside asyncio.run(), not a synchronous sleep.
    private const string AsyncioEchoAgentPy = """
        import json, sys, asyncio

        def _send(obj):
            sys.stdout.write(json.dumps(obj) + "\n")
            sys.stdout.flush()

        async def _dispatch(msg):
            method = msg.get("method", "")
            id_    = msg.get("id")
            params = msg.get("params") or {}
            if method == "initialize":
                _send({"jsonrpc": "2.0", "id": id_, "result": {
                    "protocolVersion": "2024-11-05",
                    "capabilities": {"tools": {}},
                    "serverInfo": {"name": "asyncio-echo", "version": "0.1"}
                }})
            elif method == "tools/list":
                _send({"jsonrpc": "2.0", "id": id_, "result": {"tools": []}})
            elif method == "ping" and id_ is not None:
                _send({"jsonrpc": "2.0", "id": id_, "result": {}})
            elif method == "initialized":
                pass
            elif method == "vais/agent.invoke":
                ctx   = params.get("context") or {}
                delay = float(ctx.get("delay", "0"))
                if delay > 0:
                    await asyncio.sleep(delay)
                user_msg = params.get("userMessage", "")
                state_in = params.get("state")
                turn = 1
                if state_in:
                    try:    turn = json.loads(state_in).get("turn", 0) + 1
                    except: pass
                _send({"jsonrpc": "2.0", "id": id_, "result": {
                    "assistantMessage": f"echo: {user_msg}",
                    "newState": json.dumps({"turn": turn})
                }})
            elif id_ is not None:
                _send({"jsonrpc": "2.0", "id": id_, "error": {
                    "code": -32601, "message": f"Not found: {method}"
                }})

        if hasattr(sys.stdout, "reconfigure"):
            sys.stdout.reconfigure(line_buffering=True)
        for raw in sys.stdin:
            line = raw.rstrip("\n\r")
            if not line:
                continue
            try:
                msg = json.loads(line)
            except Exception:
                continue
            asyncio.run(_dispatch(msg))
        """;

    private static string WriteTempScript()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vais-echo-{Guid.NewGuid():N}.py");
        File.WriteAllText(path, EchoAgentPy);
        return path;
    }

    private static string WriteTempAsyncioScript()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vais-asyncio-echo-{Guid.NewGuid():N}.py");
        File.WriteAllText(path, AsyncioEchoAgentPy);
        return path;
    }

    private static PythonPluginDescriptor MakeDescriptor(string scriptPath, int invokeTimeoutSeconds = 30) =>
        new(Name: "echo-agent",
            PluginDirectory: Path.GetTempPath(),
            InterpreterPath: Python,
            EntrypointPath: scriptPath,
            TargetApiVersion: "0.24",
            HandshakeTimeoutSeconds: 10,
            RestartPolicy: PythonRestartPolicy.Never,
            DeclaredTools: [],
            SecretRefs: new Dictionary<string, string>(),
            HandlerKind: PythonHandlerKind.AgentHandler,
            InvokeTimeoutSeconds: invokeTimeoutSeconds);

    private static async Task<PythonSubprocessSupervisor> StartSupervisorAsync(
        string scriptPath,
        int invokeTimeoutSeconds = 30)
    {
        var supervisor = new PythonSubprocessSupervisor(
            MakeDescriptor(scriptPath, invokeTimeoutSeconds),
            NullLoggerFactory.Instance);
        supervisor.Start();
        var ok = await supervisor.InitialHandshakeTask.WaitAsync(TimeSpan.FromSeconds(15));
        ok.Should().BeTrue(
            "Python echo agent handshake must succeed within 15 s — is Python on PATH?");
        supervisor.Status.Should().Be(PythonPluginStatus.Ready);
        return supervisor;
    }

    // -------------------------------------------------------------------------
    // Basic communication — subprocess spawns, handshakes, and responds
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FastInvoke_ReturnsEchoedMessage()
    {
        if (!Enabled) return;
        var script = WriteTempScript();
        try
        {
            await using var supervisor = await StartSupervisorAsync(script);

            var response = await supervisor.InvokeAgentAsync(
                new AgentInvokeRequest(
                    AgentId: "echo",
                    SessionId: "s1",
                    UserMessage: "hello world",
                    State: null,
                    TimeoutSeconds: 30,
                    Context: null),
                CancellationToken.None);

            response.AssistantMessage.Should().Be("echo: hello world");
            response.NewState.Should().NotBeNullOrWhiteSpace();
            supervisor.Status.Should().Be(PythonPluginStatus.Ready);
        }
        finally { File.Delete(script); }
    }

    // -------------------------------------------------------------------------
    // State round-trip — state blob is forwarded to Python and returned mutated
    // -------------------------------------------------------------------------

    [Fact]
    public async Task StateRoundTrip_TurnCountIncrements()
    {
        if (!Enabled) return;
        var script = WriteTempScript();
        try
        {
            await using var supervisor = await StartSupervisorAsync(script);

            var first = await supervisor.InvokeAgentAsync(
                new AgentInvokeRequest("echo", "s2", "first", State: null, TimeoutSeconds: 30, Context: null),
                CancellationToken.None);

            first.NewState.Should().NotBeNull();

            var second = await supervisor.InvokeAgentAsync(
                new AgentInvokeRequest("echo", "s2", "second", State: first.NewState, TimeoutSeconds: 30, Context: null),
                CancellationToken.None);

            second.AssistantMessage.Should().Be("echo: second");
            second.NewState.Should().NotBe(first.NewState, "turn count must increment between invokes");
        }
        finally { File.Delete(script); }
    }

    // -------------------------------------------------------------------------
    // Timeout bisect — this test is the diagnostic for the 2-minute Orleans bug.
    //
    // Python sleeps 30 s; InvokeTimeoutSeconds = 2 (descriptor).
    // NOTE: while the bisect override in PythonSubprocessSupervisor is active,
    //       the actual CancelAfter fires at 5 s (hardcoded), not 2 s — that's fine,
    //       the <12 s assertion still discriminates correctly.
    //
    // PASS: TimeoutException thrown in a few seconds  → CancelAfter + MCP CT path works.
    //       The 2-minute Docker bug is therefore upstream (Orleans grain boundary).
    // FAIL (no exception, returns after 30 s): MCP SDK 1.2.0 is not honoring the CT
    //       when the subprocess is blocked — the supervisor itself is the bug source.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SlowInvoke_ExceedsTimeout_ThrowsTimeoutExceptionPromptly()
    {
        if (!Enabled) return;
        var script = WriteTempScript();
        try
        {
            await using var supervisor = await StartSupervisorAsync(script, invokeTimeoutSeconds: 2);

            var sw = Stopwatch.StartNew();

            Func<Task> act = () => supervisor.InvokeAgentAsync(
                new AgentInvokeRequest(
                    AgentId: "echo",
                    SessionId: "s3",
                    UserMessage: "slow",
                    State: null,
                    TimeoutSeconds: 30,
                    Context: new Dictionary<string, string> { ["delay"] = "30" }),
                CancellationToken.None);

            await act.Should().ThrowExactlyAsync<TimeoutException>();

            sw.Stop();
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(12),
                "supervisor CancelAfter must cancel the MCP call within a few seconds, " +
                "not wait for the Python process to finish its 30-second sleep");
        }
        finally { File.Delete(script); }
    }

    // -------------------------------------------------------------------------
    // Asyncio timeout bisect — hypothesis 2 from the 2-minute Orleans bug.
    //
    // Python uses asyncio.run() per message + asyncio.sleep() for the delay —
    // matching the vais_agent_sdk._runner.run() pattern used by real plugins.
    // While the invoke is in-flight the Python stdin read-loop is blocked inside
    // asyncio.run(), so it cannot read any cancellation notification the MCP
    // client might send.
    //
    // PASS: TimeoutException thrown in a few seconds → supervisor CancelAfter
    //       works regardless of asyncio vs. synchronous dispatch on the Python
    //       side. Hypothesis 2 is ruled out; the Docker bug must be upstream
    //       (stale image, log suppression, or CT chain).
    // FAIL (no exception, returns after 30 s or hangs): McpClient.SendRequestAsync
    //       does NOT honor the CT when the Python event loop is busy — the asyncio
    //       dispatch pattern itself is the root cause.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SlowAsyncioInvoke_ExceedsTimeout_ThrowsTimeoutExceptionPromptly()
    {
        if (!Enabled) return;
        var script = WriteTempAsyncioScript();
        try
        {
            await using var supervisor = await StartSupervisorAsync(script, invokeTimeoutSeconds: 2);

            var sw = Stopwatch.StartNew();

            Func<Task> act = () => supervisor.InvokeAgentAsync(
                new AgentInvokeRequest(
                    AgentId: "asyncio-echo",
                    SessionId: "s4",
                    UserMessage: "slow",
                    State: null,
                    TimeoutSeconds: 30,
                    Context: new Dictionary<string, string> { ["delay"] = "30" }),
                CancellationToken.None);

            await act.Should().ThrowExactlyAsync<TimeoutException>();

            sw.Stop();
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(12),
                "supervisor CancelAfter must cancel the MCP call within a few seconds even when " +
                "the Python side uses asyncio.run() per message (matching vais_agent_sdk pattern)");
        }
        finally { File.Delete(script); }
    }
}
