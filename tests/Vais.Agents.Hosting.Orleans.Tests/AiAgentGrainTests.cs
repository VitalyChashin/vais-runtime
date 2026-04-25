// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Orleans.Runtime;
using Xunit;

namespace Vais.Agents.Hosting.Orleans.Tests;

[Collection(OrleansClusterCollection.CollectionName)]
public sealed class AiAgentGrainTests
{
    private readonly OrleansClusterFixture _fx;

    public AiAgentGrainTests(OrleansClusterFixture fx) => _fx = fx;

    [Fact]
    public async Task Ask_Appends_User_Then_Assistant_Turns_To_History()
    {
        var grain = _fx.Cluster.GrainFactory.GetGrain<IAiAgentGrain>("grain-ask-once");
        try
        {
            var reply = await grain.AskAsync("hello");

            reply.Should().Be("history-size=1"); // provider saw just the user turn we added

            var history = await grain.GetHistoryAsync();
            history.Should().HaveCount(2);
            history[0].Role.Should().Be(AgentChatRole.User);
            history[0].Text.Should().Be("hello");
            history[1].Role.Should().Be(AgentChatRole.Assistant);
            history[1].Text.Should().Be("history-size=1");
        }
        finally
        {
            await grain.DeleteAsync();
        }
    }

    [Fact]
    public async Task History_Persists_Across_Grain_Deactivation()
    {
        var grainId = "grain-persists";
        var grain = _fx.Cluster.GrainFactory.GetGrain<IAiAgentGrain>(grainId);
        try
        {
            await grain.AskAsync("turn-1");
            await grain.AskAsync("turn-2");
            (await grain.GetHistoryAsync()).Should().HaveCount(4);

            // Force collection of all activations — grains will be rehydrated from storage on next call.
            var management = _fx.Cluster.GrainFactory.GetGrain<IManagementGrain>(0);
            await management.ForceActivationCollection(TimeSpan.Zero);

            // Second proxy: same grain key, freshly reactivated.
            var rehydrated = _fx.Cluster.GrainFactory.GetGrain<IAiAgentGrain>(grainId);
            var rehistory = await rehydrated.GetHistoryAsync();
            rehistory.Should().HaveCount(4);
            rehistory[0].Text.Should().Be("turn-1");
            rehistory[2].Text.Should().Be("turn-2");

            // Provider should see 5 (4 prior + new user turn) when we ask again, proving the agent's
            // in-memory _history was re-seeded from InitialHistory on activation.
            var reply = await rehydrated.AskAsync("turn-3");
            reply.Should().Be("history-size=5");
        }
        finally
        {
            await grain.DeleteAsync();
        }
    }

    [Fact]
    public async Task Reset_Clears_History_But_Keeps_SystemPrompt()
    {
        var grain = _fx.Cluster.GrainFactory.GetGrain<IAiAgentGrain>("grain-reset");
        try
        {
            await grain.SetSystemPromptAsync("be concise");
            await grain.AskAsync("turn-1");
            (await grain.GetHistoryAsync()).Should().HaveCount(2);

            await grain.ResetAsync();
            (await grain.GetHistoryAsync()).Should().BeEmpty();
            (await grain.GetSystemPromptAsync()).Should().Be("be concise");
        }
        finally
        {
            await grain.DeleteAsync();
        }
    }

    [Fact]
    public async Task SystemPrompt_Persists_Across_Deactivation()
    {
        var grainId = "grain-prompt-persist";
        var grain = _fx.Cluster.GrainFactory.GetGrain<IAiAgentGrain>(grainId);
        try
        {
            await grain.SetSystemPromptAsync("system-instruction-under-test");

            var management = _fx.Cluster.GrainFactory.GetGrain<IManagementGrain>(0);
            await management.ForceActivationCollection(TimeSpan.Zero);

            var rehydrated = _fx.Cluster.GrainFactory.GetGrain<IAiAgentGrain>(grainId);
            (await rehydrated.GetSystemPromptAsync()).Should().Be("system-instruction-under-test");
        }
        finally
        {
            await grain.DeleteAsync();
        }
    }

    [Fact]
    public async Task StreamAgentAsync_Yields_TurnStarted_CompletionDelta_TurnCompleted_In_Order()
    {
        var grain = _fx.Cluster.GrainFactory.GetGrain<IAiAgentGrain>("grain-stream-order");
        try
        {
            var events = new List<AgentEvent>();
            await foreach (var evt in grain.StreamAgentAsync("hello", AgentContext.Empty))
                events.Add(evt);

            events.Should().HaveCount(3);
            events[0].Should().BeOfType<TurnStarted>().Which.UserMessage.Should().Be("hello");
            events[1].Should().BeOfType<CompletionDelta>().Which.TextDelta.Should().Be("history-size=1");
            events[2].Should().BeOfType<TurnCompleted>().Which.AssistantText.Should().Be("history-size=1");
        }
        finally
        {
            await grain.DeleteAsync();
        }
    }

    [Fact]
    public async Task StreamAgentAsync_Persists_History_After_TurnCompleted()
    {
        var grain = _fx.Cluster.GrainFactory.GetGrain<IAiAgentGrain>("grain-stream-persist");
        try
        {
            await foreach (var _ in grain.StreamAgentAsync("hello", AgentContext.Empty)) { }

            var history = await grain.GetHistoryAsync();
            history.Should().HaveCount(2);
            history[0].Role.Should().Be(AgentChatRole.User);
            history[0].Text.Should().Be("hello");
            history[1].Role.Should().Be(AgentChatRole.Assistant);
            history[1].Text.Should().Be("history-size=1");
        }
        finally
        {
            await grain.DeleteAsync();
        }
    }

    [Fact]
    public async Task StreamAgentAsync_Second_Turn_Sees_Prior_History()
    {
        var grain = _fx.Cluster.GrainFactory.GetGrain<IAiAgentGrain>("grain-stream-multiturn");
        try
        {
            await foreach (var _ in grain.StreamAgentAsync("turn-1", AgentContext.Empty)) { }

            var events = new List<AgentEvent>();
            await foreach (var evt in grain.StreamAgentAsync("turn-2", AgentContext.Empty))
                events.Add(evt);

            // Second turn: history has 2 prior turns (user+assistant from turn-1) + new user turn = 3.
            var delta = events.OfType<CompletionDelta>().Single();
            delta.TextDelta.Should().Be("history-size=3");
        }
        finally
        {
            await grain.DeleteAsync();
        }
    }

    [Fact]
    public async Task Delete_Clears_State_So_Next_Activation_Starts_Fresh()
    {
        var grainId = "grain-delete";
        var grain = _fx.Cluster.GrainFactory.GetGrain<IAiAgentGrain>(grainId);

        await grain.AskAsync("turn-1");
        await grain.AskAsync("turn-2");
        (await grain.GetHistoryAsync()).Should().HaveCount(4);

        await grain.DeleteAsync();

        // Post-delete: a new activation with the same key should read no state.
        var reborn = _fx.Cluster.GrainFactory.GetGrain<IAiAgentGrain>(grainId);
        (await reborn.GetHistoryAsync()).Should().BeEmpty();
        (await reborn.GetSystemPromptAsync()).Should().BeNull();

        await reborn.DeleteAsync();
    }
}
