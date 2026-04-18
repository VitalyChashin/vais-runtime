// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net.ServerSentEvents;
using A2A;
using FluentAssertions;
using Xunit;

namespace Vais2.Agents.Protocols.A2A.Tests;

/// <summary>
/// Unit tests for the A2A adapter's public surface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Integration scope.</b> <see cref="A2ARemoteAgentTool"/> and
/// <see cref="A2ARemoteAgentTool.CreateAsync"/> dispatch HTTP / JSON-RPC
/// requests through <see cref="A2AClient"/> and <see cref="A2ACardResolver"/>.
/// Exercising those paths requires a real A2A server or a protocol-compliant
/// HTTP fake — disproportionate for this PR. End-to-end coverage lands with
/// the v0.4 smoketest's A2A segment or a future live-server harness.
/// </para>
/// </remarks>
public sealed class A2AAgentInvocationExceptionTests
{
    [Fact]
    public void Carries_Agent_Name_And_Formats_Message()
    {
        var ex = new A2AAgentInvocationException("weather-bot", "task produced no artifacts.");

        ex.AgentName.Should().Be("weather-bot");
        ex.Message.Should().Contain("weather-bot");
        ex.Message.Should().Contain("task produced no artifacts");
    }
}

public sealed class A2ARemoteAgentToolShapeTests
{
    [Fact]
    public void Ctor_Rejects_Null_Client()
    {
        Action act = () => _ = new A2ARemoteAgentTool(client: null!, card: new AgentCard { Name = "x", Description = "x" });
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task CreateAsync_Rejects_Null_Uri()
    {
        var act = async () => await A2ARemoteAgentTool.CreateAsync(agentUrl: null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void Parameters_Schema_Declares_Required_Message_String()
    {
        // Exercise the static schema without hitting the network by sanity-checking via reflection-free path:
        // construct with a stub client + card.
        var tool = new A2ARemoteAgentTool(new StubA2AClient(), new AgentCard { Name = "alpha", Description = "desc" });

        var schema = tool.ParametersSchema;
        schema.GetProperty("type").GetString().Should().Be("object");
        schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Should().Contain("message");
        schema.GetProperty("properties").GetProperty("message").GetProperty("type").GetString().Should().Be("string");
    }
}

public sealed class SanitiseToolNameTests
{
    [Theory]
    [InlineData("weather-bot", "weather-bot")]
    [InlineData("Weather Bot", "Weather_Bot")]
    [InlineData("agent.with.dots", "agent_with_dots")]
    [InlineData("   spaced   ", "spaced")]
    [InlineData("multi!!!bangs", "multi_bangs")]
    [InlineData("name42_with-mix", "name42_with-mix")]
    public void Maps_Invalid_Chars_To_Underscores(string input, string expected)
    {
        A2ARemoteAgentTool.SanitiseToolName(input).Should().Be(expected);
    }

    [Fact]
    public void Rejects_Empty_Input()
    {
        Action act = () => A2ARemoteAgentTool.SanitiseToolName(string.Empty);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rejects_All_Invalid_Input()
    {
        // "!!!" sanitises to "_" → trimmed to empty.
        Action act = () => A2ARemoteAgentTool.SanitiseToolName("!!!");
        act.Should().Throw<ArgumentException>();
    }
}

/// <summary>Minimal <see cref="IA2AClient"/> stub — sufficient to construct a tool for shape assertions.</summary>
internal sealed class StubA2AClient : IA2AClient
{
    public Task<A2AResponse> SendMessageAsync(MessageSendParams taskSendParams, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<AgentTask> GetTaskAsync(string id, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<AgentTask> CancelTaskAsync(TaskIdParams taskIdParams, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<TaskPushNotificationConfig> SetPushNotificationAsync(TaskPushNotificationConfig pushNotificationConfig, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<TaskPushNotificationConfig> GetPushNotificationAsync(GetTaskPushNotificationConfigParams getPushNotificationConfigParams, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public IAsyncEnumerable<SseItem<A2AEvent>> SendMessageStreamingAsync(MessageSendParams taskSendParams, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public IAsyncEnumerable<SseItem<A2AEvent>> SubscribeToTaskAsync(string taskId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
