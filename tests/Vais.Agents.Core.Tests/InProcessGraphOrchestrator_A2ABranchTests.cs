// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Vais.Agents.Control.InProcess;
using Vais.Agents.Hosting.InMemory;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// v0.21 A2A protocol branch in <see cref="InProcessGraphOrchestrator"/>.
/// Verifies that a node with <see cref="GraphAgentRef.A2AUrl"/> set routes to
/// <see cref="IA2AGraphNodeInvoker"/> instead of the local lifecycle manager.
/// </summary>
public sealed class InProcessGraphOrchestrator_A2ABranchTests
{
    private static (InMemoryAgentRegistry registry, AgentLifecycleManager lifecycle) BuildLocalHarness()
    {
        var registry = new InMemoryAgentRegistry();
        var runtime = new InMemoryAgentRuntime(new FakeCompletionProvider(_ => new CompletionResponse("local-reply")));
        var lifecycle = new AgentLifecycleManager(registry, runtime);
        return (registry, lifecycle);
    }

    private static AgentGraphManifest BuildGraph(string agentId, string? a2aUrl)
    {
        var @ref = a2aUrl is null
            ? new GraphAgentRef(agentId)
            : new GraphAgentRef(agentId, "1.0", A2AUrl: a2aUrl);

        return new AgentGraphManifest(
            Id: "a2a-graph", Version: "1.0", Entry: "step",
            Nodes: new[]
            {
                new GraphNode("step", "Agent", Ref: @ref),
                new GraphNode("end", "End"),
            },
            Edges: new[] { new GraphEdge("step", "end") });
    }

    private sealed class StubA2AInvoker : IA2AGraphNodeInvoker
    {
        public List<(string Url, string Message, string? Bearer)> Calls { get; } = new();
        public string Response { get; set; } = "a2a-reply";

        public ValueTask<string> InvokeAsync(
            string a2aUrl, string message, string? bearerToken,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((a2aUrl, message, bearerToken));
            return ValueTask.FromResult(Response);
        }
    }

    [Fact]
    public async Task A2ABranch_RoutesToInvoker_NotLocalRegistry()
    {
        var (registry, lifecycle) = BuildLocalHarness();
        var stub = new StubA2AInvoker();
        var manifest = BuildGraph("a2a-agent", "https://a2a-runtime.svc");

        var orchestrator = new InProcessGraphOrchestrator(
            manifest, registry, lifecycle, new InMemoryCheckpointer(),
            a2aInvoker: stub);

        var final = await orchestrator.InvokeAsync(new Dictionary<string, JsonElement>(), new AgentContext());

        stub.Calls.Should().ContainSingle();
        stub.Calls[0].Url.Should().Be("https://a2a-runtime.svc");
    }

    [Fact]
    public async Task A2ABranch_ReturnValueMergedIntoState()
    {
        var (registry, lifecycle) = BuildLocalHarness();
        var stub = new StubA2AInvoker { Response = "a2a-answer" };
        var manifest = BuildGraph("a2a-agent", "https://a2a.svc");

        var orchestrator = new InProcessGraphOrchestrator(
            manifest, registry, lifecycle, new InMemoryCheckpointer(),
            a2aInvoker: stub);

        var final = await orchestrator.InvokeAsync(new Dictionary<string, JsonElement>(), new AgentContext());

        final.Should().ContainKey("lastAssistantText");
        final["lastAssistantText"].GetString().Should().Be("a2a-answer");
    }

    [Fact]
    public async Task A2ABranch_JSONResponseExtractsFields()
    {
        var (registry, lifecycle) = BuildLocalHarness();
        var stub = new StubA2AInvoker
        {
            Response = """{"summary":"The user asked about refunds","confidence":0.95}"""
        };
        var manifest = BuildGraph("a2a-agent", "https://a2a.svc");
        manifest = manifest with
        {
            Nodes = manifest.Nodes.Select(n => n.Kind == "Agent"
                ? n with { StateBindings = new GraphStateBindings(Output: new[] { "summary", "confidence" }) }
                : n).ToList()
        };

        var orchestrator = new InProcessGraphOrchestrator(
            manifest, registry, lifecycle, new InMemoryCheckpointer(),
            a2aInvoker: stub);

        var final = await orchestrator.InvokeAsync(new Dictionary<string, JsonElement>(), new AgentContext());

        final.Should().ContainKey("summary");
        final["summary"].GetString().Should().Be("The user asked about refunds");
        final.Should().ContainKey("confidence");
        final["confidence"].GetDouble().Should().BeApproximately(0.95, 1e-9);
    }

    [Fact]
    public async Task A2ABranch_ForwardsBearerToken()
    {
        var (registry, lifecycle) = BuildLocalHarness();
        var stub = new StubA2AInvoker();
        var manifest = BuildGraph("a2a-agent", "https://a2a.svc");

        var orchestrator = new InProcessGraphOrchestrator(
            manifest, registry, lifecycle, new InMemoryCheckpointer(),
            a2aInvoker: stub,
            bearerToken: "a2a-tok");

        await orchestrator.InvokeAsync(new Dictionary<string, JsonElement>(), new AgentContext());

        stub.Calls[0].Bearer.Should().Be("a2a-tok");
    }

    [Fact]
    public async Task A2ABranch_NoInvokerSupplied_ThrowsInvalidOperationException()
    {
        var (registry, lifecycle) = BuildLocalHarness();
        var manifest = BuildGraph("a2a-agent", "https://a2a.svc");

        var orchestrator = new InProcessGraphOrchestrator(
            manifest, registry, lifecycle, new InMemoryCheckpointer());

        var act = async () => await orchestrator.InvokeAsync(new Dictionary<string, JsonElement>(), new AgentContext());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*IA2AGraphNodeInvoker*");
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
        var stub = new StubA2AInvoker();
        var manifest = BuildGraph("local-agent", a2aUrl: null);

        var orchestrator = new InProcessGraphOrchestrator(
            manifest, registry, lifecycle, new InMemoryCheckpointer(),
            a2aInvoker: stub);

        var final = await orchestrator.InvokeAsync(new Dictionary<string, JsonElement>(), new AgentContext());

        stub.Calls.Should().BeEmpty();
        final.Should().ContainKey("lastAssistantText");
    }

    [Fact]
    public async Task RuntimeUrl_StillRoutesToRemoteInvoker_WhenA2AInvokerPresent()
    {
        var (registry, lifecycle) = BuildLocalHarness();
        var a2aStub = new StubA2AInvoker();
        var httpStub = new StubRemoteInvoker();
        var manifest = new AgentGraphManifest(
            Id: "mixed-graph", Version: "1.0", Entry: "http-step",
            Nodes: new[]
            {
                new GraphNode("http-step", "Agent", Ref: new GraphAgentRef("http-agent", RuntimeUrl: "https://http.svc")),
                new GraphNode("end", "End"),
            },
            Edges: new[] { new GraphEdge("http-step", "end") });

        var orchestrator = new InProcessGraphOrchestrator(
            manifest, registry, lifecycle, new InMemoryCheckpointer(),
            remoteInvoker: httpStub,
            a2aInvoker: a2aStub);

        await orchestrator.InvokeAsync(new Dictionary<string, JsonElement>(), new AgentContext());

        a2aStub.Calls.Should().BeEmpty();
        httpStub.Calls.Should().ContainSingle();
    }

    private sealed class StubRemoteInvoker : IAgentRemoteInvoker
    {
        public List<(string Url, AgentHandle Handle, AgentInvocationRequest Req, string? Bearer)> Calls { get; } = new();
        public AgentInvocationResult Response { get; set; } = new("http-reply");

        public ValueTask<AgentInvocationResult> InvokeAsync(
            string runtimeUrl, AgentHandle handle, AgentInvocationRequest request,
            string? bearerToken, CancellationToken cancellationToken = default)
        {
            Calls.Add((runtimeUrl, handle, request, bearerToken));
            return ValueTask.FromResult(Response);
        }

        public async IAsyncEnumerable<AgentEvent> StreamAsync(
            string runtimeUrl, AgentHandle handle, AgentInvocationRequest request,
            string? bearerToken, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls.Add((runtimeUrl, handle, request, bearerToken));
            var at = DateTimeOffset.UtcNow;
            var ctx = new AgentContext();
            yield return new TurnStarted(at, ctx, request.Text);
            yield return new TurnCompleted(at, ctx, Response.Text, null, null, null, TimeSpan.Zero);
            await Task.CompletedTask;
        }
    }
}
