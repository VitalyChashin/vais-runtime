// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Vais.Agents.Core;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Python.Tests;

/// <summary>
/// Unit tests for <see cref="PythonAgentShimFactory"/>.
/// </summary>
public sealed class PythonAgentShimFactoryTests
{
    private static readonly TimeSpan[] FastBackoff = [TimeSpan.FromMilliseconds(20)];

    private static PythonPluginDescriptor MakeAgentDescriptor(string handlerTypeName = "Acme.MyAgent") =>
        new(
            Name: "factory-test-plugin",
            PluginDirectory: "/fake",
            InterpreterPath: "/fake/python",
            EntrypointPath: "/fake/server.py",
            TargetApiVersion: "0.23",
            HandshakeTimeoutSeconds: 5,
            RestartPolicy: PythonRestartPolicy.Never,
            DeclaredTools: [],
            SecretRefs: new Dictionary<string, string>(),
            HandlerKind: PythonHandlerKind.AgentHandler,
            HandlerTypeName: handlerTypeName,
            InvokeTimeoutSeconds: 60);

    private static AgentManifest MakeManifest(string agentId = "agent-1", string? inlinePrompt = null) =>
        new(
            Id: agentId,
            Version: "1",
            Handler: new AgentHandlerRef("Acme.MyAgent"),
            Protocols: [],
            Tools: [])
        {
            SystemPrompt = inlinePrompt is null ? null : new SystemPromptSpec(Inline: inlinePrompt),
        };

    // Using the 2-arg production constructor — no process is spawned until Start() is called.
    private static PythonSubprocessSupervisor MakeIdleSupervisor(string handlerTypeName = "Acme.MyAgent")
    {
        var descriptor = MakeAgentDescriptor(handlerTypeName);
        return new PythonSubprocessSupervisor(descriptor, NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task HandlerTypeName_MatchesDescriptor()
    {
        await using var supervisor = MakeIdleSupervisor("My.CustomAgent");
        var factory = new PythonAgentShimFactory(supervisor, maxStateSizeBytes: 0);

        factory.HandlerTypeName.Should().Be("My.CustomAgent");
    }

    [Fact]
    public async Task CreateAsync_ReturnsShimWithCorrectAgentId()
    {
        await using var supervisor = MakeIdleSupervisor();
        var factory = new PythonAgentShimFactory(supervisor, maxStateSizeBytes: 0);

        var agent = await factory.CreateAsync(MakeManifest("my-agent"), serviceProvider: null!);

        agent.Session.AgentId.Should().Be("my-agent");
    }

    [Fact]
    public async Task CreateAsync_ReturnsShimWithEmptyHistory()
    {
        await using var supervisor = MakeIdleSupervisor();
        var factory = new PythonAgentShimFactory(supervisor, maxStateSizeBytes: 0);

        var agent = await factory.CreateAsync(MakeManifest(), serviceProvider: null!);

        agent.History.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_WithInlineSystemPrompt_SetsShimSystemPrompt()
    {
        await using var supervisor = MakeIdleSupervisor();
        var factory = new PythonAgentShimFactory(supervisor, maxStateSizeBytes: 0);

        var agent = await factory.CreateAsync(
            MakeManifest(inlinePrompt: "You are a helpful assistant."),
            serviceProvider: null!);

        agent.SystemPrompt.Should().Be("You are a helpful assistant.");
    }

    [Fact]
    public async Task CreateAsync_WithoutSystemPrompt_SystemPromptIsNull()
    {
        await using var supervisor = MakeIdleSupervisor();
        var factory = new PythonAgentShimFactory(supervisor, maxStateSizeBytes: 0);

        var agent = await factory.CreateAsync(MakeManifest(), serviceProvider: null!);

        agent.SystemPrompt.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_TwoCalls_ReturnDistinctAgentInstances()
    {
        await using var supervisor = MakeIdleSupervisor();
        var factory = new PythonAgentShimFactory(supervisor, maxStateSizeBytes: 0);

        var agent1 = await factory.CreateAsync(MakeManifest("a"), serviceProvider: null!);
        var agent2 = await factory.CreateAsync(MakeManifest("b"), serviceProvider: null!);

        agent1.Should().NotBeSameAs(agent2);
        agent1.Session.Should().NotBeSameAs(agent2.Session);
        agent1.Session.AgentId.Should().Be("a");
        agent2.Session.AgentId.Should().Be("b");
    }
}
