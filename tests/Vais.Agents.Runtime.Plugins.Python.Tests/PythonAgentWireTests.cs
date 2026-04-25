// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Python.Tests;

/// <summary>
/// End-to-end wire tests for <see cref="PythonSubprocessSupervisor.InvokeAgentAsync"/>
/// against the hermetic <c>langgraph-researcher</c> sample.
///
/// Opt-in: set <c>VAIS_RUN_PYTHON_PLUGIN_TESTS=1</c> to run them. The Python
/// interpreter and the <c>vais-agent-sdk</c> + sample packages must be installed
/// before running:
/// <code>
/// pip install samples/python-agent-sdk
/// pip install samples/PluginAgentLangGraphResearcher/langgraph-researcher
/// set VAIS_RUN_PYTHON_PLUGIN_TESTS=1
/// dotnet test tests/Vais.Agents.Runtime.Plugins.Python.Tests
/// </code>
/// </summary>
[Collection("PythonAgentWire")] // serialised collection avoids parallel subprocess conflicts
public sealed class PythonAgentWireTests
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

    private static string ResolveServerPath()
    {
        // Walk up from the test binary directory to find the solution root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Vais.Agents.sln")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException(
                "Could not find solution root from " + AppContext.BaseDirectory);
        return Path.Combine(
            dir.FullName,
            "samples", "PluginAgentLangGraphResearcher", "langgraph-researcher",
            "src", "langgraph_researcher", "server.py");
    }

    private static PythonPluginDescriptor MakeDescriptor()
    {
        var serverPath = ResolveServerPath();
        return new PythonPluginDescriptor(
            Name: "langgraph-researcher",
            PluginDirectory: Path.GetDirectoryName(serverPath)!,
            InterpreterPath: Python,
            EntrypointPath: serverPath,
            TargetApiVersion: "0.24",
            HandshakeTimeoutSeconds: 20,
            RestartPolicy: PythonRestartPolicy.Never,
            DeclaredTools: [],
            SecretRefs: new Dictionary<string, string>(),
            HandlerKind: PythonHandlerKind.AgentHandler,
            HandlerTypeName: "langgraph_researcher.agent.ResearcherAgent",
            InvokeTimeoutSeconds: 30);
    }

    private static async Task<PythonSubprocessSupervisor> StartSupervisorAsync()
    {
        var supervisor = new PythonSubprocessSupervisor(MakeDescriptor(), NullLoggerFactory.Instance);
        supervisor.Start();
        var ok = await supervisor.InitialHandshakeTask.WaitAsync(TimeSpan.FromSeconds(25));
        ok.Should().BeTrue("subprocess handshake must succeed within 25 s — check Python interpreter and packages");
        supervisor.Status.Should().Be(PythonPluginStatus.Ready);
        return supervisor;
    }

    // -------------------------------------------------------------------------
    // Golden path — first invoke (no prior state)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GoldenPath_FirstInvoke_ReturnsAssistantMessageAndNewState()
    {
        if (!Enabled) return;

        await using var supervisor = await StartSupervisorAsync();

        var response = await supervisor.InvokeAgentAsync(
            new AgentInvokeRequest(
                AgentId: "wire-agent",
                SessionId: "sess-1",
                UserMessage: "What is quantum computing?",
                State: null,
                TimeoutSeconds: 30,
                Context: null),
            CancellationToken.None);

        response.AssistantMessage.Should().NotBeNullOrWhiteSpace();
        response.NewState.Should().NotBeNullOrWhiteSpace("agent must return state on first turn");
    }

    // -------------------------------------------------------------------------
    // State round-trip — second invoke receives previous state
    // -------------------------------------------------------------------------

    [Fact]
    public async Task StateRoundTrip_SecondInvoke_ReceivesStateFromFirstAndProducesNewState()
    {
        if (!Enabled) return;

        await using var supervisor = await StartSupervisorAsync();

        var first = await supervisor.InvokeAgentAsync(
            new AgentInvokeRequest("wire-agent", "sess-2", "Tell me about AI.",
                State: null, TimeoutSeconds: 30, Context: null),
            CancellationToken.None);

        first.NewState.Should().NotBeNull();

        var second = await supervisor.InvokeAgentAsync(
            new AgentInvokeRequest("wire-agent", "sess-2", "Continue the research.",
                State: first.NewState, TimeoutSeconds: 30, Context: null),
            CancellationToken.None);

        second.AssistantMessage.Should().NotBeNullOrWhiteSpace();
        second.NewState.Should().NotBe(first.NewState,
            "state blob must change between turns (turn_count increments)");
    }

    // -------------------------------------------------------------------------
    // Supervisor stays Ready after a successful invoke
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AfterInvoke_SupervisorStatus_RemainsReady()
    {
        if (!Enabled) return;

        await using var supervisor = await StartSupervisorAsync();

        await supervisor.InvokeAgentAsync(
            new AgentInvokeRequest("wire-agent", "sess-3", "Hello.",
                State: null, TimeoutSeconds: 30, Context: null),
            CancellationToken.None);

        supervisor.Status.Should().Be(PythonPluginStatus.Ready);
    }
}

[CollectionDefinition("PythonAgentWire")]
public sealed class PythonAgentWireCollection : ICollectionFixture<object> { }
