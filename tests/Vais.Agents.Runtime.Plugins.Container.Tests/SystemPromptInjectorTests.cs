// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using NSubstitute;
using Vais.Agents.Runtime.Instantiation;
using Vais.Agents.Runtime.Plugins.Container.Preprocessing;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Container.Tests;

public sealed class SystemPromptInjectorTests
{
    private static readonly IReadOnlyList<ChatTurn> s_seedMessage =
        [new ChatTurn(AgentChatRole.User, "hello")];

    private static AgentPreprocessorContext MakeContext(
        SystemPromptSpec? manifestPrompt = null,
        string? grainSystemPrompt = null,
        IReadOnlyList<ChatTurn>? history = null) =>
        new(
            "test-agent",
            "session-1",
            new AgentManifest("test-agent", "1.0", new AgentHandlerRef("Test"), [], [])
            {
                SystemPrompt = manifestPrompt,
            },
            new FakeGrainState(grainSystemPrompt, history ?? []),
            AgentContext.Empty);

    [Fact]
    public void Order_Is_Ten()
    {
        new SystemPromptInjector(null, null).Order.Should().Be(10);
    }

    [Fact]
    public async Task ProcessAsync_NullSpec_NoGrainOverride_ReturnsUnchanged()
    {
        var injector = new SystemPromptInjector(null, null);
        var ctx = MakeContext(manifestPrompt: null, grainSystemPrompt: null);

        var result = await injector.ProcessAsync(ctx, s_seedMessage);

        result.Should().BeSameAs(s_seedMessage);
    }

    [Fact]
    public async Task ProcessAsync_EmptyInline_ReturnsUnchanged()
    {
        var injector = new SystemPromptInjector(null, null);
        var ctx = MakeContext(manifestPrompt: new SystemPromptSpec(Inline: ""));

        var result = await injector.ProcessAsync(ctx, s_seedMessage);

        result.Should().BeSameAs(s_seedMessage);
    }

    [Fact]
    public async Task ProcessAsync_GrainStateOverride_PrependsThatPrompt()
    {
        var injector = new SystemPromptInjector(null, null);
        var ctx = MakeContext(
            manifestPrompt: new SystemPromptSpec(Inline: "manifest prompt"),
            grainSystemPrompt: "grain override");

        var result = await injector.ProcessAsync(ctx, s_seedMessage);

        result.Should().HaveCount(2);
        result[0].Role.Should().Be(AgentChatRole.System);
        result[0].Text.Should().Be("grain override");
        result[1].Text.Should().Be("hello");
    }

    [Fact]
    public async Task ProcessAsync_InlineSpec_PrependsThatPrompt()
    {
        var injector = new SystemPromptInjector(null, null);
        var ctx = MakeContext(manifestPrompt: new SystemPromptSpec(Inline: "You are X"));

        var result = await injector.ProcessAsync(ctx, s_seedMessage);

        result.Should().HaveCount(2);
        result[0].Role.Should().Be(AgentChatRole.System);
        result[0].Text.Should().Be("You are X");
    }

    [Fact]
    public async Task ProcessAsync_TemplateRef_ResolvesAndPrepends()
    {
        var registry = Substitute.For<IPromptTemplateRegistry>();
        registry.Get("my-template").Returns("resolved prompt");
        var injector = new SystemPromptInjector(registry, null);
        var ctx = MakeContext(manifestPrompt: new SystemPromptSpec(TemplateRef: "my-template"));

        var result = await injector.ProcessAsync(ctx, s_seedMessage);

        result.Should().HaveCount(2);
        result[0].Text.Should().Be("resolved prompt");
    }

    [Fact]
    public async Task ProcessAsync_TemplateRef_AppliesVariableSubstitution()
    {
        var registry = Substitute.For<IPromptTemplateRegistry>();
        registry.Get("t").Returns("Hello {{name}}");
        var injector = new SystemPromptInjector(registry, null);
        var ctx = MakeContext(manifestPrompt: new SystemPromptSpec(
            TemplateRef: "t",
            Variables: new Dictionary<string, string> { ["name"] = "World" }));

        var result = await injector.ProcessAsync(ctx, s_seedMessage);

        result[0].Text.Should().Be("Hello World");
    }

    [Fact]
    public async Task ProcessAsync_TemplateRef_ThrowsWhenRegistryAbsent()
    {
        var injector = new SystemPromptInjector(null, null);
        var ctx = MakeContext(manifestPrompt: new SystemPromptSpec(TemplateRef: "t"));

        var act = () => injector.ProcessAsync(ctx, s_seedMessage).AsTask();
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{ContainerPluginUrns.SystemPromptResolutionFailed}*");
    }

    [Fact]
    public async Task ProcessAsync_TemplateRef_ThrowsWhenNameNotFound()
    {
        var registry = Substitute.For<IPromptTemplateRegistry>();
        registry.Get(Arg.Any<string>()).Returns((string?)null);
        var injector = new SystemPromptInjector(registry, null);
        var ctx = MakeContext(manifestPrompt: new SystemPromptSpec(TemplateRef: "missing"));

        var act = () => injector.ProcessAsync(ctx, s_seedMessage).AsTask();
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{ContainerPluginUrns.SystemPromptResolutionFailed}*");
    }

    [Fact]
    public async Task ProcessAsync_FileRef_LoadsAndPrepends()
    {
        var loader = Substitute.For<IPromptFileLoader>();
        loader.LoadAsync("f.txt", Arg.Any<CancellationToken>())
              .Returns(new ValueTask<string>("file content"));
        var injector = new SystemPromptInjector(null, loader);
        var ctx = MakeContext(manifestPrompt: new SystemPromptSpec(FileRef: "f.txt"));

        var result = await injector.ProcessAsync(ctx, s_seedMessage);

        result.Should().HaveCount(2);
        result[0].Text.Should().Be("file content");
    }

    [Fact]
    public async Task ProcessAsync_FileRef_ThrowsWhenLoaderAbsent()
    {
        var injector = new SystemPromptInjector(null, null);
        var ctx = MakeContext(manifestPrompt: new SystemPromptSpec(FileRef: "f.txt"));

        var act = () => injector.ProcessAsync(ctx, s_seedMessage).AsTask();
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{ContainerPluginUrns.SystemPromptResolutionFailed}*");
    }

    [Fact]
    public async Task ProcessAsync_SystemPromptPrepended_HistoryTurnsFollow()
    {
        // Simulates the full chain: HistoryAssembler ran first, seed is already [User1, Asst1, User2].
        var injector = new SystemPromptInjector(null, null);
        IReadOnlyList<ChatTurn> assembled =
        [
            new ChatTurn(AgentChatRole.User, "user-1"),
            new ChatTurn(AgentChatRole.Assistant, "asst-1"),
            new ChatTurn(AgentChatRole.User, "user-2"),
        ];
        var ctx = MakeContext(manifestPrompt: new SystemPromptSpec(Inline: "You are X"));

        var result = await injector.ProcessAsync(ctx, assembled);

        result.Should().HaveCount(4);
        result[0].Role.Should().Be(AgentChatRole.System);
        result[0].Text.Should().Be("You are X");
        result[1].Text.Should().Be("user-1");
        result[2].Text.Should().Be("asst-1");
        result[3].Text.Should().Be("user-2");
    }

    private sealed class FakeGrainState : IAgentGrainStateView
    {
        public FakeGrainState(string? systemPrompt, IReadOnlyList<ChatTurn> history)
        {
            SystemPrompt = systemPrompt;
            History = history;
        }

        public string? SystemPrompt { get; }
        public IReadOnlyList<ChatTurn> History { get; }
        public string? OpaqueState => null;
    }
}
