// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Vais.Agents.Control.InProcess;
using Vais.Agents.Hosting.InMemory;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// v0.20 Pillar E: cross-runtime branch in <see cref="InProcessGraphOrchestrator"/>.
/// Verifies that a node with <see cref="GraphAgentRef.RuntimeUrl"/> set routes to
/// <see cref="IAgentRemoteInvoker"/> instead of the local lifecycle manager.
/// </summary>
public sealed class InProcessGraphOrchestrator_RemoteBranchTests
{
    // ─── helpers ──────────────────────────────────────────────────────────

    private static (InMemoryAgentRegistry registry, AgentLifecycleManager lifecycle) BuildLocalHarness()
    {
        var registry = new InMemoryAgentRegistry();
        var runtime = new InMemoryAgentRuntime(new FakeCompletionProvider(_ => new CompletionResponse("local-reply")));
        var lifecycle = new AgentLifecycleManager(registry, runtime);
        return (registry, lifecycle);
    }

    private static AgentGraphManifest BuildGraph(string agentId, string? runtimeUrl)
    {
        var @ref = runtimeUrl is null
            ? new GraphAgentRef(agentId)
            : new GraphAgentRef(agentId, "1.0", runtimeUrl);

        return new AgentGraphManifest(
            Id: "remote-graph", Version: "1.0", Entry: "step",
            Nodes: new[]
            {
                new GraphNode("step", "Agent", Ref: @ref),
                new GraphNode("end", "End"),
            },
            Edges: new[] { new GraphEdge("step", "end") });
    }

    // ─── stub invoker ─────────────────────────────────────────────────────

    private sealed class StubRemoteInvoker : IAgentRemoteInvoker
    {
        public List<(string Url, AgentHandle Handle, AgentInvocationRequest Req, string? Bearer)> Calls { get; } = new();
        public AgentInvocationResult Response { get; set; } = new("remote-reply");

        public ValueTask<AgentInvocationResult> InvokeAsync(
            string runtimeUrl, AgentHandle handle, AgentInvocationRequest request,
            string? bearerToken, CancellationToken cancellationToken = default)
        {
            Calls.Add((runtimeUrl, handle, request, bearerToken));
            return ValueTask.FromResult(Response);
        }
    }

    private sealed class ThrowingRemoteInvoker : IAgentRemoteInvoker
    {
        public ValueTask<AgentInvocationResult> InvokeAsync(
            string runtimeUrl, AgentHandle handle, AgentInvocationRequest request,
            string? bearerToken, CancellationToken cancellationToken = default)
            => throw new RemoteAgentInvocationException(runtimeUrl, HttpStatusCode.ServiceUnavailable);
    }

    // ─── tests ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RemoteBranch_RoutesToInvoker_NotLocalRegistry()
    {
        var (registry, lifecycle) = BuildLocalHarness();
        var stub = new StubRemoteInvoker();
        var manifest = BuildGraph("remote-agent", "https://runtime-b.svc");

        var orchestrator = new InProcessGraphOrchestrator(
            manifest, registry, lifecycle, new InMemoryCheckpointer(),
            remoteInvoker: stub);

        var final = await orchestrator.InvokeAsync(new Dictionary<string, JsonElement>(), new AgentContext());

        stub.Calls.Should().ContainSingle();
        stub.Calls[0].Url.Should().Be("https://runtime-b.svc");
        stub.Calls[0].Handle.AgentId.Should().Be("remote-agent");
    }

    [Fact]
    public async Task RemoteBranch_ReturnValueMergedIntoState()
    {
        var (registry, lifecycle) = BuildLocalHarness();
        var stub = new StubRemoteInvoker { Response = new AgentInvocationResult("remote-answer") };
        var manifest = BuildGraph("remote-agent", "https://runtime-b.svc");

        var orchestrator = new InProcessGraphOrchestrator(
            manifest, registry, lifecycle, new InMemoryCheckpointer(),
            remoteInvoker: stub);

        var final = await orchestrator.InvokeAsync(new Dictionary<string, JsonElement>(), new AgentContext());

        final.Should().ContainKey("lastAssistantText");
        final["lastAssistantText"].GetString().Should().Be("remote-answer");
    }

    [Fact]
    public async Task RemoteBranch_ForwardsBearerToken()
    {
        var (registry, lifecycle) = BuildLocalHarness();
        var stub = new StubRemoteInvoker();
        var manifest = BuildGraph("remote-agent", "https://runtime-b.svc");

        var orchestrator = new InProcessGraphOrchestrator(
            manifest, registry, lifecycle, new InMemoryCheckpointer(),
            remoteInvoker: stub,
            bearerToken: "tok-xyz");

        await orchestrator.InvokeAsync(new Dictionary<string, JsonElement>(), new AgentContext());

        stub.Calls[0].Bearer.Should().Be("tok-xyz");
    }

    [Fact]
    public async Task LocalBranch_StillResolvesViaRegistry()
    {
        var (registry, lifecycle) = BuildLocalHarness();
        await lifecycle.CreateAsync(new AgentManifest(
            Id: "local-agent", Version: "1.0",
            Handler: new AgentHandlerRef("declarative"),
            Protocols: new[] { new ProtocolBinding("Http") },
            Tools: Array.Empty<ToolRef>()));
        var stub = new StubRemoteInvoker();
        var manifest = BuildGraph("local-agent", runtimeUrl: null);

        var orchestrator = new InProcessGraphOrchestrator(
            manifest, registry, lifecycle, new InMemoryCheckpointer(),
            remoteInvoker: stub);

        var final = await orchestrator.InvokeAsync(new Dictionary<string, JsonElement>(), new AgentContext());

        stub.Calls.Should().BeEmpty();
        final.Should().ContainKey("lastAssistantText");
    }

    [Fact]
    public async Task RemoteBranch_NoInvokerSupplied_ThrowsInvalidOperationException()
    {
        var (registry, lifecycle) = BuildLocalHarness();
        var manifest = BuildGraph("remote-agent", "https://runtime-b.svc");

        var orchestrator = new InProcessGraphOrchestrator(
            manifest, registry, lifecycle, new InMemoryCheckpointer());

        var act = async () => await orchestrator.InvokeAsync(new Dictionary<string, JsonElement>(), new AgentContext());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*IAgentRemoteInvoker*");
    }

    [Fact]
    public async Task RemoteBranch_InvokerThrows_PropagatesToCaller()
    {
        var (registry, lifecycle) = BuildLocalHarness();
        var throwing = new ThrowingRemoteInvoker();
        var manifest = BuildGraph("remote-agent", "https://runtime-b.svc");

        var orchestrator = new InProcessGraphOrchestrator(
            manifest, registry, lifecycle, new InMemoryCheckpointer(),
            remoteInvoker: throwing);

        var act = async () => await orchestrator.InvokeAsync(new Dictionary<string, JsonElement>(), new AgentContext());

        await act.Should().ThrowAsync<RemoteAgentInvocationException>()
            .Where(e => e.Status == HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task RemoteBranch_VersionPassedThrough()
    {
        var (registry, lifecycle) = BuildLocalHarness();
        var stub = new StubRemoteInvoker();
        var manifest = BuildGraph("remote-agent", "https://runtime-b.svc");

        var orchestrator = new InProcessGraphOrchestrator(
            manifest, registry, lifecycle, new InMemoryCheckpointer(),
            remoteInvoker: stub);

        await orchestrator.InvokeAsync(new Dictionary<string, JsonElement>(), new AgentContext());

        stub.Calls[0].Handle.Version.Should().Be("1.0");
    }
}
