// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais2.Agents.Core.Tests;

public sealed class StatefulAiAgentTests
{
    [Fact]
    public void Ctor_Rejects_Null_Provider()
    {
        Action act = () => _ = new StatefulAiAgent(provider: null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("provider");
    }

    [Fact]
    public async Task AskAsync_Rejects_Empty_User_Message()
    {
        var agent = new StatefulAiAgent(new FakeCompletionProvider());

        Func<Task> act = async () => await agent.AskAsync("   ");

        (await act.Should().ThrowAsync<ArgumentException>())
            .Which.ParamName.Should().Be("userMessage");
    }

    [Fact]
    public async Task AskAsync_Appends_User_Then_Assistant_To_History()
    {
        var provider = new FakeCompletionProvider(_ => new CompletionResponse("Paris."));
        var agent = new StatefulAiAgent(provider);

        var reply = await agent.AskAsync("What is the capital of France?");

        reply.Should().Be("Paris.");
        agent.History.Should().HaveCount(2);
        agent.History[0].Should().Be(new ChatTurn(AgentChatRole.User, "What is the capital of France?"));
        agent.History[1].Should().Be(new ChatTurn(AgentChatRole.Assistant, "Paris."));
    }

    [Fact]
    public async Task AskAsync_Forwards_SystemPrompt_To_Provider()
    {
        var provider = new FakeCompletionProvider();
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions { SystemPrompt = "Be terse." });

        await agent.AskAsync("Hi");

        provider.Received.Should().ContainSingle()
            .Which.SystemPrompt.Should().Be("Be terse.");
    }

    [Fact]
    public async Task AskAsync_Observes_Current_SystemPrompt_Per_Turn()
    {
        var provider = new FakeCompletionProvider();
        var agent = new StatefulAiAgent(provider);

        agent.SystemPrompt = "first";
        await agent.AskAsync("q1");

        agent.SystemPrompt = "second";
        await agent.AskAsync("q2");

        provider.Received.Should().HaveCount(2);
        provider.Received[0].SystemPrompt.Should().Be("first");
        provider.Received[1].SystemPrompt.Should().Be("second");
    }

    [Fact]
    public async Task AskAsync_Passes_Cumulative_History_Each_Turn()
    {
        var provider = new FakeCompletionProvider(req =>
            new CompletionResponse($"reply-{req.History.Count}"));
        var agent = new StatefulAiAgent(provider);

        await agent.AskAsync("one");
        await agent.AskAsync("two");
        await agent.AskAsync("three");

        provider.Received.Should().HaveCount(3);
        // History at the moment of call = previous pairs + just-appended user message.
        provider.Received[0].History.Count.Should().Be(1);
        provider.Received[1].History.Count.Should().Be(3);
        provider.Received[2].History.Count.Should().Be(5);
    }

    [Fact]
    public async Task Reset_Clears_History_But_Keeps_SystemPrompt()
    {
        var agent = new StatefulAiAgent(new FakeCompletionProvider(), new StatefulAgentOptions { SystemPrompt = "keep me" });
        await agent.AskAsync("hi");
        agent.History.Should().NotBeEmpty();

        agent.Reset();

        agent.History.Should().BeEmpty();
        agent.SystemPrompt.Should().Be("keep me");
    }

    [Fact]
    public async Task AskAsync_Propagates_Cancellation()
    {
        using var cts = new CancellationTokenSource();
        var provider = new FakeCompletionProvider(_ =>
        {
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
            return new CompletionResponse("unreachable");
        });
        var agent = new StatefulAiAgent(provider);

        Func<Task> act = async () => await agent.AskAsync("hi", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
