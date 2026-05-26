// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using FluentAssertions;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Core.Tests.Ontology;

/// <summary>
/// C2-1 verify gate: the capability-map projection builds a stable per-coordinator map
/// from <see cref="AgentManifest.LocalAgents"/> cross-joined with the <c>agent:</c>-sourced
/// entries in <see cref="AgentManifest.Tools"/>; cached per coordinator; invalidatable.
/// </summary>
public sealed class AgentCapabilityMapTests
{
    [Fact]
    public async Task Builder_EnumeratesRegisteredSubAgents_CrossJoinedFromToolsAndLocalAgents()
    {
        var registry = NewRegistry();
        var coord = new AgentManifest(
            "coordinator", "1.0",
            new AgentHandlerRef("default", "maf"),
            [new ProtocolBinding("openai")],
            Tools: [
                new ToolRef("reviewer", Source: "agent:reviewerLocal"),
                new ToolRef("tester", Source: "agent:testerLocal"),
            ])
        {
            LocalAgents = [
                new LocalAgentRef("reviewerLocal", AgentId: "code-reviewer"),
                new LocalAgentRef("testerLocal",   AgentId: "test-runner"),
            ],
        };
        Register(registry, coord);
        Register(registry, new AgentManifest(
            "code-reviewer", "1.0",
            new AgentHandlerRef("default", "maf"), [new ProtocolBinding("openai")], Tools: [])
        {
            Description = "Reviews code diffs for correctness.",
            Labels = new Dictionary<string, string> { ["role"] = "review", ["risk"] = "read" },
        });
        Register(registry, new AgentManifest(
            "test-runner", "1.0",
            new AgentHandlerRef("default", "maf"), [new ProtocolBinding("openai")], Tools: [])
        {
            Description = "Runs unit tests against the working tree.",
            Labels = new Dictionary<string, string> { ["role"] = "verify" },
        });

        var map = await new AgentCapabilityMapBuilder(registry).BuildAsync("coordinator");

        map.CoordinatorAgentId.Should().Be("coordinator");
        map.SubAgents.Should().HaveCount(2);

        var reviewer = map.SubAgents.Single(s => s.ToolName == "reviewer");
        reviewer.AgentId.Should().Be("code-reviewer");
        reviewer.Description.Should().Be("Reviews code diffs for correctness.");
        reviewer.Tags.Should().BeEquivalentTo(["role:review", "risk:read"]);
        reviewer.Mode.Should().Be(LocalAgentInvocationMode.Blocking);

        var tester = map.SubAgents.Single(s => s.ToolName == "tester");
        tester.AgentId.Should().Be("test-runner");
        tester.Tags.Should().Contain("role:verify");
    }

    [Fact]
    public async Task Builder_LocalRefDescriptionOverridesTargetManifestDescription()
    {
        var registry = NewRegistry();
        Register(registry, new AgentManifest(
            "coord", "1.0", new AgentHandlerRef("default", "maf"), [new ProtocolBinding("openai")],
            Tools: [new ToolRef("worker", Source: "agent:workerLocal")])
        {
            LocalAgents = [new LocalAgentRef("workerLocal", AgentId: "worker-bot") { Description = "Coordinator-supplied summary." }],
        });
        Register(registry, new AgentManifest(
            "worker-bot", "1.0", new AgentHandlerRef("default", "maf"), [new ProtocolBinding("openai")], Tools: [])
        {
            Description = "Original target description.",
        });

        var map = await new AgentCapabilityMapBuilder(registry).BuildAsync("coord");

        map.SubAgents.Single().Description.Should().Be("Coordinator-supplied summary.",
            "LocalAgentRef.Description override wins over target manifest description");
    }

    [Fact]
    public async Task Builder_EmptyMapWhenCoordinatorHasNoLocalAgents()
    {
        var registry = NewRegistry();
        Register(registry, new AgentManifest(
            "lonely", "1.0", new AgentHandlerRef("default", "maf"), [new ProtocolBinding("openai")], Tools: []));

        var map = await new AgentCapabilityMapBuilder(registry).BuildAsync("lonely");

        map.SubAgents.Should().BeEmpty();
        map.ToCompactText().Should().BeEmpty("an empty map renders as empty so callers can append unconditionally");
    }

    [Fact]
    public async Task Builder_EmptyMapWhenCoordinatorNotRegistered()
    {
        var registry = NewRegistry();
        var map = await new AgentCapabilityMapBuilder(registry).BuildAsync("ghost");

        map.Should().BeEquivalentTo(CapabilityMap.Empty("ghost"));
    }

    [Fact]
    public async Task Builder_SkipsToolRefWhoseLocalAgentNameDoesNotResolve()
    {
        var registry = NewRegistry();
        Register(registry, new AgentManifest(
            "coord", "1.0", new AgentHandlerRef("default", "maf"), [new ProtocolBinding("openai")],
            Tools: [
                new ToolRef("real",  Source: "agent:resolvable"),
                new ToolRef("ghost", Source: "agent:nonexistent"),
            ])
        {
            LocalAgents = [new LocalAgentRef("resolvable", AgentId: "real-agent")],
        });
        Register(registry, new AgentManifest(
            "real-agent", "1.0", new AgentHandlerRef("default", "maf"), [new ProtocolBinding("openai")], Tools: []));

        var map = await new AgentCapabilityMapBuilder(registry).BuildAsync("coord");

        map.SubAgents.Should().ContainSingle().Which.ToolName.Should().Be("real");
    }

    [Fact]
    public async Task Builder_CachesPerCoordinator_SecondCallReturnsSameInstance()
    {
        var registry = NewRegistry();
        Register(registry, new AgentManifest(
            "coord", "1.0", new AgentHandlerRef("default", "maf"), [new ProtocolBinding("openai")],
            Tools: [new ToolRef("w", Source: "agent:workerLocal")])
        {
            LocalAgents = [new LocalAgentRef("workerLocal", AgentId: "worker")],
        });
        Register(registry, new AgentManifest(
            "worker", "1.0", new AgentHandlerRef("default", "maf"), [new ProtocolBinding("openai")], Tools: []));

        var builder = new AgentCapabilityMapBuilder(registry);
        var first = await builder.BuildAsync("coord");
        var second = await builder.BuildAsync("coord");

        second.Should().BeSameAs(first);
    }

    [Fact]
    public async Task Builder_InvalidateRebuildsOnNextCall()
    {
        var registry = NewRegistry();
        Register(registry, new AgentManifest(
            "coord", "1.0", new AgentHandlerRef("default", "maf"), [new ProtocolBinding("openai")],
            Tools: [new ToolRef("w", Source: "agent:workerLocal")])
        {
            LocalAgents = [new LocalAgentRef("workerLocal", AgentId: "worker")],
        });
        Register(registry, new AgentManifest(
            "worker", "1.0", new AgentHandlerRef("default", "maf"), [new ProtocolBinding("openai")], Tools: []));

        var builder = new AgentCapabilityMapBuilder(registry);
        var first = await builder.BuildAsync("coord");
        builder.Invalidate("coord");
        var second = await builder.BuildAsync("coord");

        second.Should().NotBeSameAs(first, "cache was invalidated, a fresh map must have been built");
        second.Should().BeEquivalentTo(first, "same registry inputs ⇒ same logical content");
    }

    // ── compact text rendering ────────────────────────────────────────────────

    [Fact]
    public void ToCompactText_RendersOneLinePerSubAgentWithTagsAndDescription()
    {
        var map = new CapabilityMap("coord", [
            new SubAgentCapability("reviewer", "code-reviewer", "Reviews code diffs.",
                ["role:review", "risk:read"], LocalAgentInvocationMode.Blocking),
            new SubAgentCapability("deployer", "deploy-bot", "Deploys to dev.",
                ["risk:Destructive"], LocalAgentInvocationMode.Background),
        ]);

        var text = map.ToCompactText();

        text.Should().Contain("Your team");
        text.Should().Contain("- reviewer: Reviews code diffs. [role:review, risk:read]");
        text.Should().Contain("- deployer: Deploys to dev. [risk:Destructive] (background)");
    }

    [Fact]
    public void ToCompactText_OmitsTagBracketAndModeWhenEmptyOrDefault()
    {
        var map = new CapabilityMap("coord", [
            new SubAgentCapability("plain", "plain-bot", "Plain.", [], LocalAgentInvocationMode.Blocking),
        ]);

        var text = map.ToCompactText();

        text.Should().Contain("- plain: Plain.\n");
        text.Should().NotContain("[");
        text.Should().NotContain("(blocking)");
    }

    // ── argument guards ──────────────────────────────────────────────────────

    [Fact]
    public async Task Builder_RejectsNullOrWhitespaceCoordinatorId()
    {
        var builder = new AgentCapabilityMapBuilder(NewRegistry());
        await FluentActions.Invoking(async () => await builder.BuildAsync(null!)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Invoking(async () => await builder.BuildAsync(" ")).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void Constructor_RejectsNullRegistry()
    {
        FluentActions.Invoking(() => new AgentCapabilityMapBuilder(null!)).Should().Throw<ArgumentNullException>();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static FakeAgentRegistry NewRegistry() => new();

    private static void Register(FakeAgentRegistry registry, AgentManifest m) => registry.Add(m);

    private sealed class FakeAgentRegistry : IAgentRegistry
    {
        private readonly Dictionary<string, AgentManifest> _byId = new(StringComparer.Ordinal);

        public void Add(AgentManifest m) => _byId[m.Id] = m;

        public ValueTask<AgentManifest?> GetAsync(string id, string? version = null, CancellationToken cancellationToken = default)
            => new(_byId.TryGetValue(id, out var m) ? m : null);

        public async IAsyncEnumerable<AgentManifest> ListAsync(string? labelPrefix = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            foreach (var m in _byId.Values) yield return m;
        }
    }
}
