// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Cli.Commands;
using Xunit;

namespace Vais.Agents.Cli.Tests;

public sealed class InitCommandTests
{
    [Fact]
    public void BuildScaffold_Defaults_UsesOpenaiAndToolCalling()
    {
        var yaml = InitCommand.BuildScaffold("chat-bot", modelProvider: null, agentMode: null);

        yaml.Should().Contain("id: chat-bot");
        yaml.Should().Contain("provider: openai");
        yaml.Should().Contain("agentMode: toolCalling");
    }

    [Fact]
    public void BuildScaffold_Overrides_ApplyToYaml()
    {
        var yaml = InitCommand.BuildScaffold("chat-bot", modelProvider: "anthropic", agentMode: "sgr");

        yaml.Should().Contain("provider: anthropic");
        yaml.Should().Contain("agentMode: sgr");
    }

    [Fact]
    public void BuildScaffold_IncludesHandlerAndSystemPromptStubs()
    {
        var yaml = InitCommand.BuildScaffold("x", modelProvider: null, agentMode: null);

        yaml.Should().Contain("handler:");
        yaml.Should().Contain("typeName: Vais.Agents.Samples.ChatAgent");
        yaml.Should().Contain("systemPrompt:");
        yaml.Should().Contain("You are a helpful assistant.");
    }
}
