// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests;

public sealed class CompletionRequestTests
{
    [Fact]
    public void ResponseFormat_Defaults_To_Null()
    {
        var request = new CompletionRequest(Array.Empty<ChatTurn>());

        request.ResponseFormat.Should().BeNull();
    }

    [Fact]
    public void ResponseFormat_RoundTripsThroughCtor()
    {
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"label":{"type":"string"}}}""").RootElement;
        var spec = new ResponseFormatSpec(schema, SchemaName: "my-schema", Strict: true);

        var request = new CompletionRequest(
            Array.Empty<ChatTurn>(),
            ResponseFormat: spec);

        request.ResponseFormat.Should().NotBeNull();
        request.ResponseFormat!.SchemaName.Should().Be("my-schema");
        request.ResponseFormat.Strict.Should().BeTrue();
        request.ResponseFormat.Schema.GetRawText().Should().Be(schema.GetRawText());
    }

    [Fact]
    public void Existing_Params_Compile_And_Default_Unchanged()
    {
        var history = new[] { new ChatTurn(AgentChatRole.User, "hi") };
        var request = new CompletionRequest(history, SystemPrompt: "sys", Temperature: 0.5f, MaxTokens: 100);

        request.SystemPrompt.Should().Be("sys");
        request.Temperature.Should().Be(0.5f);
        request.MaxTokens.Should().Be(100);
        request.Tools.Should().BeNull();
        request.ResponseFormat.Should().BeNull();
    }
}
