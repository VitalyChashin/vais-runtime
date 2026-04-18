// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais2.Agents.Core.Tests;

public sealed class AgentManifestRecordTests
{
    [Fact]
    public void Record_Equality_Holds_Across_Fields()
    {
        var handler = new AgentHandlerRef("My.Agent.Type");
        var protocols = new[] { new ProtocolBinding("Http") };
        var tools = new[] { new ToolRef("search") };

        var a = new AgentManifest("support", "1.0", handler, protocols, tools);
        var b = new AgentManifest("support", "1.0", handler, protocols, tools);

        a.Should().Be(b);
    }

    [Fact]
    public void Record_Equality_Distinguishes_Version()
    {
        var handler = new AgentHandlerRef("My.Agent.Type");
        var a = new AgentManifest("support", "1.0", handler, Array.Empty<ProtocolBinding>(), Array.Empty<ToolRef>());
        var b = new AgentManifest("support", "2.0", handler, Array.Empty<ProtocolBinding>(), Array.Empty<ToolRef>());

        a.Should().NotBe(b);
    }

    [Fact]
    public void Optional_Fields_Default_To_Null()
    {
        var manifest = new AgentManifest(
            "x", "1",
            new AgentHandlerRef("T"),
            Array.Empty<ProtocolBinding>(),
            Array.Empty<ToolRef>());

        manifest.Memory.Should().BeNull();
        manifest.Identity.Should().BeNull();
        manifest.Autoscaling.Should().BeNull();
        manifest.Description.Should().BeNull();
        manifest.Labels.Should().BeNull();
    }

    [Fact]
    public void AgentHandle_Three_Field_Shape()
    {
        var h = new AgentHandle("support", "1.0", "instance-42");
        h.AgentId.Should().Be("support");
        h.Version.Should().Be("1.0");
        h.InstanceId.Should().Be("instance-42");

        var nullInstance = new AgentHandle("support", "1.0");
        nullInstance.InstanceId.Should().BeNull();
    }

    [Fact]
    public void AgentStatus_Has_Five_Values()
    {
        Enum.GetValues<AgentStatus>().Should().BeEquivalentTo(new[]
        {
            AgentStatus.Unknown,
            AgentStatus.Active,
            AgentStatus.Idle,
            AgentStatus.Paused,
            AgentStatus.Terminated,
        });
    }
}

public sealed class InMemoryAgentRegistryTests
{
    [Fact]
    public async Task Register_Then_Get_Exact_Version_Roundtrips()
    {
        var registry = new InMemoryAgentRegistry();
        var m = new AgentManifest("support", "1.0", new AgentHandlerRef("T"), Array.Empty<ProtocolBinding>(), Array.Empty<ToolRef>());
        registry.Register(m);

        (await registry.GetAsync("support", "1.0")).Should().BeSameAs(m);
    }

    [Fact]
    public async Task Get_Null_Version_Returns_Latest_Lexicographically()
    {
        var registry = new InMemoryAgentRegistry();
        var handler = new AgentHandlerRef("T");
        registry.Register(new AgentManifest("support", "1.0", handler, Array.Empty<ProtocolBinding>(), Array.Empty<ToolRef>()));
        registry.Register(new AgentManifest("support", "2.0", handler, Array.Empty<ProtocolBinding>(), Array.Empty<ToolRef>(), Description: "v2"));
        registry.Register(new AgentManifest("support", "1.5", handler, Array.Empty<ProtocolBinding>(), Array.Empty<ToolRef>()));

        var latest = await registry.GetAsync("support");
        latest.Should().NotBeNull();
        latest!.Version.Should().Be("2.0");
        latest.Description.Should().Be("v2");
    }

    [Fact]
    public async Task Get_Missing_Returns_Null()
    {
        var registry = new InMemoryAgentRegistry();
        (await registry.GetAsync("missing")).Should().BeNull();
        (await registry.GetAsync("missing", "1.0")).Should().BeNull();
    }

    [Fact]
    public async Task List_Returns_All_Manifests_When_No_Filter()
    {
        var registry = new InMemoryAgentRegistry();
        var h = new AgentHandlerRef("T");
        registry.Register(new AgentManifest("a", "1", h, Array.Empty<ProtocolBinding>(), Array.Empty<ToolRef>()));
        registry.Register(new AgentManifest("b", "1", h, Array.Empty<ProtocolBinding>(), Array.Empty<ToolRef>()));

        var list = new List<AgentManifest>();
        await foreach (var m in registry.ListAsync())
        {
            list.Add(m);
        }

        list.Should().HaveCount(2);
    }

    [Fact]
    public async Task List_Filters_By_Label_Prefix()
    {
        var registry = new InMemoryAgentRegistry();
        var h = new AgentHandlerRef("T");

        registry.Register(new AgentManifest("a", "1", h, Array.Empty<ProtocolBinding>(), Array.Empty<ToolRef>(),
            Labels: new Dictionary<string, string> { ["team"] = "support" }));
        registry.Register(new AgentManifest("b", "1", h, Array.Empty<ProtocolBinding>(), Array.Empty<ToolRef>(),
            Labels: new Dictionary<string, string> { ["team"] = "sales" }));
        registry.Register(new AgentManifest("c", "1", h, Array.Empty<ProtocolBinding>(), Array.Empty<ToolRef>(),
            Labels: new Dictionary<string, string> { ["env"] = "prod" }));

        var matches = new List<AgentManifest>();
        await foreach (var m in registry.ListAsync("team:"))
        {
            matches.Add(m);
        }

        matches.Should().HaveCount(2);
        matches.Select(m => m.Id).Should().BeEquivalentTo(new[] { "a", "b" });
    }

    [Fact]
    public async Task Remove_Deletes_Manifest()
    {
        var registry = new InMemoryAgentRegistry();
        var h = new AgentHandlerRef("T");
        registry.Register(new AgentManifest("a", "1", h, Array.Empty<ProtocolBinding>(), Array.Empty<ToolRef>()));

        registry.Remove("a", "1").Should().BeTrue();
        (await registry.GetAsync("a", "1")).Should().BeNull();
        registry.Remove("a", "1").Should().BeFalse();
    }

    [Fact]
    public void Register_Rejects_Null_Manifest()
    {
        var registry = new InMemoryAgentRegistry();
        Action act = () => registry.Register(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
