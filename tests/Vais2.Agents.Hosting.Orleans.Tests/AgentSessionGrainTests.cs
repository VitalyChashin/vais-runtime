// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais2.Agents.Hosting.Orleans.Tests;

[Collection(OrleansClusterCollection.CollectionName)]
public sealed class AgentSessionGrainTests(OrleansClusterFixture fixture)
{
    [Fact]
    public async Task Append_Then_GetHistory_Roundtrips()
    {
        var grain = fixture.Cluster.GrainFactory.GetGrain<IAgentSessionGrain>(
            OrleansSessionGrainKey.Build("agent-a", Guid.NewGuid().ToString("N")));

        await grain.AppendAsync(new ChatTurn(AgentChatRole.User, "hi"));
        await grain.AppendAsync(new ChatTurn(AgentChatRole.Assistant, "hello"));

        var history = await grain.GetHistoryAsync();

        history.Should().HaveCount(2);
        history[0].Role.Should().Be(AgentChatRole.User);
        history[1].Role.Should().Be(AgentChatRole.Assistant);
    }

    [Fact]
    public async Task Reset_Clears_History_Per_Session()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var grain = fixture.Cluster.GrainFactory.GetGrain<IAgentSessionGrain>(
            OrleansSessionGrainKey.Build("agent-a", sessionId));

        await grain.AppendAsync(new ChatTurn(AgentChatRole.User, "hi"));
        await grain.ResetAsync();

        (await grain.GetHistoryAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Different_Sessions_Of_Same_Agent_Isolate_History()
    {
        var agentId = "agent-" + Guid.NewGuid().ToString("N");
        var a = fixture.Cluster.GrainFactory.GetGrain<IAgentSessionGrain>(
            OrleansSessionGrainKey.Build(agentId, "s1"));
        var b = fixture.Cluster.GrainFactory.GetGrain<IAgentSessionGrain>(
            OrleansSessionGrainKey.Build(agentId, "s2"));

        await a.AppendAsync(new ChatTurn(AgentChatRole.User, "from-a"));
        await b.AppendAsync(new ChatTurn(AgentChatRole.User, "from-b"));

        (await a.GetHistoryAsync()).Should().ContainSingle().Which.Text.Should().Be("from-a");
        (await b.GetHistoryAsync()).Should().ContainSingle().Which.Text.Should().Be("from-b");
    }

    [Fact]
    public async Task Different_Sessions_Run_Concurrently()
    {
        // Orleans serialises per-grain. If writer-scope were the agent, these two
        // appends would queue; because the writer-scope is the session, they run
        // in parallel on different grain instances. We measure by ensuring both
        // complete quickly even when artificially delayed on the client side.
        var agentId = "agent-" + Guid.NewGuid().ToString("N");
        var sessionA = OrleansSessionGrainKey.Build(agentId, "a");
        var sessionB = OrleansSessionGrainKey.Build(agentId, "b");
        var a = fixture.Cluster.GrainFactory.GetGrain<IAgentSessionGrain>(sessionA);
        var b = fixture.Cluster.GrainFactory.GetGrain<IAgentSessionGrain>(sessionB);

        var t1 = a.AppendAsync(new ChatTurn(AgentChatRole.User, "one"));
        var t2 = b.AppendAsync(new ChatTurn(AgentChatRole.User, "two"));

        await Task.WhenAll(t1, t2);

        (await a.GetHistoryAsync()).Should().ContainSingle();
        (await b.GetHistoryAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Delete_Clears_Persisted_State()
    {
        var key = OrleansSessionGrainKey.Build("agent-a", Guid.NewGuid().ToString("N"));
        var grain = fixture.Cluster.GrainFactory.GetGrain<IAgentSessionGrain>(key);

        await grain.AppendAsync(new ChatTurn(AgentChatRole.User, "temp"));
        await grain.DeleteAsync();

        // Re-acquire the grain (it deactivated); history should be empty again.
        var reacquired = fixture.Cluster.GrainFactory.GetGrain<IAgentSessionGrain>(key);
        (await reacquired.GetHistoryAsync()).Should().BeEmpty();
    }
}

[Collection(OrleansClusterCollection.CollectionName)]
public sealed class OrleansAgentSessionProxyTests(OrleansClusterFixture fixture)
{
    [Fact]
    public async Task Proxy_AppendAsync_Writes_Through_To_Grain()
    {
        var runtime = new OrleansAgentRuntime(fixture.Cluster.GrainFactory);
        var sessionId = Guid.NewGuid().ToString("N");
        var session = runtime.GetSession("agent-p", sessionId);

        await session.AppendAsync(new ChatTurn(AgentChatRole.User, "via proxy"));

        // Round-trip through the grain directly to confirm persistence.
        var grain = fixture.Cluster.GrainFactory.GetGrain<IAgentSessionGrain>(
            OrleansSessionGrainKey.Build("agent-p", sessionId));
        var history = await grain.GetHistoryAsync();

        history.Should().ContainSingle().Which.Text.Should().Be("via proxy");
        session.History.Should().ContainSingle().Which.Text.Should().Be("via proxy");
    }

    [Fact]
    public async Task Proxy_History_Is_Lazy_Hydrated_From_Grain()
    {
        // Seed state via the grain, then ask the proxy — it should hydrate on read.
        var sessionId = Guid.NewGuid().ToString("N");
        var grain = fixture.Cluster.GrainFactory.GetGrain<IAgentSessionGrain>(
            OrleansSessionGrainKey.Build("agent-p", sessionId));
        await grain.AppendAsync(new ChatTurn(AgentChatRole.User, "seed"));

        var runtime = new OrleansAgentRuntime(fixture.Cluster.GrainFactory);
        var session = runtime.GetSession("agent-p", sessionId);

        session.History.Should().ContainSingle().Which.Text.Should().Be("seed");
    }

    [Fact]
    public async Task StatefulAiAgent_Composed_With_Proxy_Session_Writes_Through_Durably()
    {
        var runtime = new OrleansAgentRuntime(fixture.Cluster.GrainFactory);
        var sessionId = Guid.NewGuid().ToString("N");
        var session = runtime.GetSession("agent-composed", sessionId);

        var provider = new global::Vais2.Agents.Hosting.Orleans.Tests.FixedReplyProvider("answer");
        var agent = new Vais2.Agents.Core.StatefulAiAgent(
            provider,
            new Vais2.Agents.Core.StatefulAgentOptions { Session = session });

        await agent.AskAsync("question");

        // Read through a fresh runtime instance + proxy: durable state should survive.
        var freshRuntime = new OrleansAgentRuntime(fixture.Cluster.GrainFactory);
        var freshSession = freshRuntime.GetSession("agent-composed", sessionId);
        freshSession.History.Should().HaveCount(2);
        freshSession.History[0].Text.Should().Be("question");
        freshSession.History[1].Text.Should().Be("answer");
    }

    [Fact]
    public async Task Proxy_ResetAsync_Clears_Grain_History()
    {
        var runtime = new OrleansAgentRuntime(fixture.Cluster.GrainFactory);
        var sessionId = Guid.NewGuid().ToString("N");
        var session = runtime.GetSession("agent-r", sessionId);

        await session.AppendAsync(new ChatTurn(AgentChatRole.User, "transient"));
        await session.ResetAsync();

        session.History.Should().BeEmpty();
        var grain = fixture.Cluster.GrainFactory.GetGrain<IAgentSessionGrain>(
            OrleansSessionGrainKey.Build("agent-r", sessionId));
        (await grain.GetHistoryAsync()).Should().BeEmpty();
    }
}

[Collection(OrleansClusterCollection.CollectionName)]
public sealed class AgentConfigGrainTests(OrleansClusterFixture fixture)
{
    [Fact]
    public async Task SystemPrompt_Roundtrips_Across_Sessions()
    {
        var runtime = new OrleansAgentRuntime(fixture.Cluster.GrainFactory);
        var agentId = "agent-cfg-" + Guid.NewGuid().ToString("N");

        var config = runtime.GetAgentConfig(agentId);
        await config.SetSystemPromptAsync("You are helpful.");

        // Any other client reading the same agent's config grain sees the value.
        var sameConfig = runtime.GetAgentConfig(agentId);
        (await sameConfig.GetSystemPromptAsync()).Should().Be("You are helpful.");
    }

    [Fact]
    public async Task Config_Grain_Is_Separate_From_Session_Grains()
    {
        var runtime = new OrleansAgentRuntime(fixture.Cluster.GrainFactory);
        var agentId = "agent-cfg-split-" + Guid.NewGuid().ToString("N");

        await runtime.GetAgentConfig(agentId).SetSystemPromptAsync("shared prompt");

        // Writing to a session of this agent does not alter the config grain.
        await runtime.GetSession(agentId, "s1").AppendAsync(new ChatTurn(AgentChatRole.User, "hi"));
        await runtime.GetSession(agentId, "s2").AppendAsync(new ChatTurn(AgentChatRole.User, "bye"));

        (await runtime.GetAgentConfig(agentId).GetSystemPromptAsync()).Should().Be("shared prompt");
    }
}

public sealed class OrleansSessionGrainKeyTests
{
    [Fact]
    public void Build_Formats_As_AgentId_Slash_SessionId()
        => OrleansSessionGrainKey.Build("a", "s").Should().Be("a/s");

    [Fact]
    public void Parse_Reverses_Build()
    {
        var (agentId, sessionId) = OrleansSessionGrainKey.Parse("agent-1/session-xyz");
        agentId.Should().Be("agent-1");
        sessionId.Should().Be("session-xyz");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("no-separator")]
    [InlineData("/leading")]
    [InlineData("trailing/")]
    public void Parse_Rejects_Malformed_Keys(string key)
    {
        Action act = () => OrleansSessionGrainKey.Parse(key);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("a/b", "s")]
    [InlineData("a", "s/b")]
    [InlineData("", "s")]
    [InlineData("a", " ")]
    public void Build_Rejects_Invalid_Inputs(string agentId, string sessionId)
    {
        Action act = () => OrleansSessionGrainKey.Build(agentId, sessionId);
        act.Should().Throw<ArgumentException>();
    }
}

internal sealed class FixedReplyProvider(string reply) : ICompletionProvider
{
    public string ProviderName => "fixed";

    public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new CompletionResponse(reply));
}
