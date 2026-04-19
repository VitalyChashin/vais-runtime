// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests;

public sealed class AgentSessionTests
{
    [Fact]
    public void Ctor_Rejects_Null_Or_Empty_AgentId()
    {
        Action act = () => _ = new InMemoryAgentSession(agentId: " ");
        act.Should().Throw<ArgumentException>().WithParameterName("agentId");
    }

    [Fact]
    public void Ctor_Generates_SessionId_When_None_Supplied()
    {
        var a = new InMemoryAgentSession("agent-a");
        var b = new InMemoryAgentSession("agent-a");

        a.SessionId.Should().NotBeNullOrWhiteSpace();
        b.SessionId.Should().NotBe(a.SessionId);
    }

    [Fact]
    public void Ctor_Honours_Supplied_SessionId()
    {
        var s = new InMemoryAgentSession("agent-a", sessionId: "session-1");
        s.SessionId.Should().Be("session-1");
        s.AgentId.Should().Be("agent-a");
    }

    [Fact]
    public void Ctor_Copies_Initial_History()
    {
        var seed = new[]
        {
            new ChatTurn(AgentChatRole.User, "hi"),
            new ChatTurn(AgentChatRole.Assistant, "hello"),
        };
        var s = new InMemoryAgentSession("agent-a", initialHistory: seed);
        s.History.Should().BeEquivalentTo(seed);
    }

    [Fact]
    public async Task AppendAsync_Adds_Turn_In_Order()
    {
        var s = new InMemoryAgentSession("agent-a");
        await s.AppendAsync(new ChatTurn(AgentChatRole.User, "one"));
        await s.AppendAsync(new ChatTurn(AgentChatRole.Assistant, "two"));

        s.History.Should().HaveCount(2);
        s.History[0].Text.Should().Be("one");
        s.History[1].Text.Should().Be("two");
    }

    [Fact]
    public async Task AppendAsync_Rejects_Null_Turn()
    {
        var s = new InMemoryAgentSession("agent-a");
        Func<Task> act = async () => await s.AppendAsync(turn: null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ResetAsync_Clears_History_But_Preserves_Identity()
    {
        var s = new InMemoryAgentSession("agent-a", sessionId: "sess-1");
        await s.AppendAsync(new ChatTurn(AgentChatRole.User, "hi"));

        await s.ResetAsync();

        s.History.Should().BeEmpty();
        s.SessionId.Should().Be("sess-1");
        s.AgentId.Should().Be("agent-a");
    }
}

public sealed class StatefulAiAgentSessionIntegrationTests
{
    [Fact]
    public void Default_Session_Is_Created_When_None_Supplied()
    {
        var agent = new StatefulAiAgent(new FakeCompletionProvider());

        agent.Session.Should().NotBeNull();
        agent.Session.AgentId.Should().Be("agent");
        agent.Session.SessionId.Should().NotBeNullOrWhiteSpace();
        agent.Session.History.Should().BeEmpty();
    }

    [Fact]
    public void Default_Session_Uses_AgentName_As_AgentId()
    {
        var agent = new StatefulAiAgent(
            new FakeCompletionProvider(),
            new StatefulAgentOptions { AgentName = "planner" });

        agent.Session.AgentId.Should().Be("planner");
    }

    [Fact]
    public void InitialHistory_Seeds_Default_Session()
    {
        var seed = new[] { new ChatTurn(AgentChatRole.User, "seed-1") };
        var agent = new StatefulAiAgent(
            new FakeCompletionProvider(),
            new StatefulAgentOptions { InitialHistory = seed });

        agent.Session.History.Should().HaveCount(1);
        agent.Session.History[0].Text.Should().Be("seed-1");
        agent.History.Should().BeEquivalentTo(agent.Session.History);
    }

    [Fact]
    public void Both_Session_And_InitialHistory_Throws()
    {
        var seed = new[] { new ChatTurn(AgentChatRole.User, "seed") };
        var session = new InMemoryAgentSession("agent-a");

        Action act = () => _ = new StatefulAiAgent(
            new FakeCompletionProvider(),
            new StatefulAgentOptions { Session = session, InitialHistory = seed });

        act.Should().Throw<ArgumentException>().WithParameterName("options");
    }

    [Fact]
    public async Task AskAsync_Writes_Through_The_Supplied_Session()
    {
        var session = new InMemoryAgentSession("agent-a", sessionId: "sess-1");
        var provider = new FakeCompletionProvider(_ => new CompletionResponse("reply"));
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions { Session = session });

        await agent.AskAsync("hello");

        session.History.Should().HaveCount(2);
        session.History[0].Should().Be(new ChatTurn(AgentChatRole.User, "hello"));
        session.History[1].Should().Be(new ChatTurn(AgentChatRole.Assistant, "reply"));
        agent.History.Should().BeEquivalentTo(session.History);
    }

    [Fact]
    public async Task IAiAgent_History_Is_Live_Shim_Over_Session_History()
    {
        var agent = new StatefulAiAgent(new FakeCompletionProvider(_ => new CompletionResponse("ok")));

        await agent.AskAsync("one");
        agent.History.Should().HaveSameCount(agent.Session.History);

        await agent.Session.AppendAsync(new ChatTurn(AgentChatRole.User, "direct-append"));
        agent.History.Should().HaveCount(3);
        agent.History[2].Text.Should().Be("direct-append");
    }

    [Fact]
    public async Task Reset_Clears_Both_Views()
    {
        var agent = new StatefulAiAgent(new FakeCompletionProvider(_ => new CompletionResponse("ok")));

        await agent.AskAsync("one");
        agent.Reset();

        agent.History.Should().BeEmpty();
        agent.Session.History.Should().BeEmpty();
    }

    [Fact]
    public async Task Two_Agents_Sharing_One_Session_See_Combined_History()
    {
        var shared = new InMemoryAgentSession("agent-shared");
        var a = new StatefulAiAgent(
            new FakeCompletionProvider(_ => new CompletionResponse("reply-a")),
            new StatefulAgentOptions { Session = shared });
        var b = new StatefulAiAgent(
            new FakeCompletionProvider(_ => new CompletionResponse("reply-b")),
            new StatefulAgentOptions { Session = shared });

        await a.AskAsync("from-a");
        await b.AskAsync("from-b");

        shared.History.Should().HaveCount(4);
        shared.History[0].Text.Should().Be("from-a");
        shared.History[1].Text.Should().Be("reply-a");
        shared.History[2].Text.Should().Be("from-b");
        shared.History[3].Text.Should().Be("reply-b");
    }
}
