// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using NSubstitute;
using Xunit;

namespace Vais.Agents.Ai.MicrosoftAgentFramework.Tests;

public sealed class MafCompletionProviderTests
{
    [Fact]
    public void SupportsResponseFormat_Returns_True()
    {
        var client = Substitute.For<IChatClient>();
        client.GetService(typeof(ChatClientMetadata), Arg.Any<object?>()).Returns(new ChatClientMetadata("openai", null, "gpt-4o"));

        var provider = new MafCompletionProvider(client);

        provider.SupportsResponseFormat.Should().BeTrue();
    }

    [Fact]
    public async Task ResponseFormat_SetsChatOptionsResponseFormat_On_CompleteAsync()
    {
        ChatOptions? capturedOptions = null;

        var client = Substitute.For<IChatClient>();
        client.GetService(typeof(ChatClientMetadata), Arg.Any<object?>()).Returns(new ChatClientMetadata("openai", null, "gpt-4o"));
        client.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Do<ChatOptions?>(o => capturedOptions = o),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}")));

        var schema = JsonDocument.Parse("""{"type":"object","properties":{"label":{"type":"string"}}}""").RootElement;
        var request = new CompletionRequest(
            new[] { new ChatTurn(AgentChatRole.User, "classify this") },
            ResponseFormat: new ResponseFormatSpec(schema, SchemaName: "ticket"));

        var provider = new MafCompletionProvider(client);
        await provider.CompleteAsync(request);

        capturedOptions.Should().NotBeNull();
        capturedOptions!.ResponseFormat.Should().NotBeNull();
        capturedOptions.ResponseFormat.Should().BeOfType<ChatResponseFormatJson>();
    }

    [Fact]
    public async Task ResponseFormat_Null_Does_Not_Set_ChatOptionsResponseFormat()
    {
        ChatOptions? capturedOptions = null;

        var client = Substitute.For<IChatClient>();
        client.GetService(typeof(ChatClientMetadata), Arg.Any<object?>()).Returns(new ChatClientMetadata("openai", null, "gpt-4o"));
        client.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Do<ChatOptions?>(o => capturedOptions = o),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "hello")));

        var request = new CompletionRequest(
            new[] { new ChatTurn(AgentChatRole.User, "hi") });

        var provider = new MafCompletionProvider(client);
        await provider.CompleteAsync(request);

        capturedOptions.Should().NotBeNull();
        capturedOptions!.ResponseFormat.Should().BeNull();
    }
}
