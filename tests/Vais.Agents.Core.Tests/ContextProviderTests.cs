// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests;

public sealed class ContextProviderTests
{
    [Fact]
    public async Task Zero_Providers_Leaves_Request_Unchanged()
    {
        CompletionRequest? captured = null;
        var provider = new FakeCompletionProvider(req => { captured = req; return new CompletionResponse("ok"); });
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions { SystemPrompt = "base" });

        await agent.AskAsync("hi");

        captured.Should().NotBeNull();
        captured!.SystemPrompt.Should().Be("base");
        captured.History.Should().HaveCount(1);
        captured.Tools.Should().BeNull();
    }

    [Fact]
    public async Task Single_Provider_Adds_SystemPromptAddendum_With_Separator()
    {
        CompletionRequest? captured = null;
        var provider = new FakeCompletionProvider(req => { captured = req; return new CompletionResponse("ok"); });
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            SystemPrompt = "base",
            ContextProviders = new[] { new FixedContributionProvider(new ContextContribution(SystemPromptAddendum: "extra")) },
        });

        await agent.AskAsync("hi");

        captured!.SystemPrompt.Should().Be("base\n\nextra");
    }

    [Fact]
    public async Task Provider_SystemPrompt_Without_Base_Is_Just_The_Addendum()
    {
        CompletionRequest? captured = null;
        var provider = new FakeCompletionProvider(req => { captured = req; return new CompletionResponse("ok"); });
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            ContextProviders = new[] { new FixedContributionProvider(new ContextContribution(SystemPromptAddendum: "only")) },
        });

        await agent.AskAsync("hi");

        captured!.SystemPrompt.Should().Be("only");
    }

    [Fact]
    public async Task Multiple_Providers_Concatenate_Addenda_In_Order()
    {
        CompletionRequest? captured = null;
        var provider = new FakeCompletionProvider(req => { captured = req; return new CompletionResponse("ok"); });
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            SystemPrompt = "base",
            ContextProviders = new[]
            {
                new FixedContributionProvider(new ContextContribution(SystemPromptAddendum: "first")),
                new FixedContributionProvider(new ContextContribution(SystemPromptAddendum: "second")),
            },
        });

        await agent.AskAsync("hi");

        captured!.SystemPrompt.Should().Be("base\n\nfirst\n\nsecond");
    }

    [Fact]
    public async Task Provider_Can_Inject_History_Appended_After_Session_History()
    {
        CompletionRequest? captured = null;
        var provider = new FakeCompletionProvider(req => { captured = req; return new CompletionResponse("ok"); });
        var injected = new[]
        {
            new ChatTurn(AgentChatRole.Assistant, "prior context 1"),
            new ChatTurn(AgentChatRole.User, "prior context 2"),
        };
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            ContextProviders = new[] { new FixedContributionProvider(new ContextContribution(InjectedHistory: injected)) },
        });

        await agent.AskAsync("hi");

        captured!.History.Should().HaveCount(3);
        captured.History[0].Text.Should().Be("hi");                  // session user turn
        captured.History[1].Text.Should().Be("prior context 1");     // injected
        captured.History[2].Text.Should().Be("prior context 2");
    }

    [Fact]
    public async Task Provider_Can_Add_Tools_Merged_With_Existing_Registry()
    {
        CompletionRequest? captured = null;
        var provider = new FakeCompletionProvider(req => { captured = req; return new CompletionResponse("ok"); });
        var baseRegistry = new InMemoryToolRegistry(new FakeTool("base-tool"));
        var extraTool = new FakeTool("extra-tool");
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            ToolRegistry = baseRegistry,
            ContextProviders = new[]
            {
                new FixedContributionProvider(new ContextContribution(AdditionalTools: new ITool[] { extraTool })),
            },
        });

        await agent.AskAsync("hi");

        captured!.Tools.Should().HaveCount(2);
        captured.Tools!.Select(t => t.Name).Should().BeEquivalentTo(new[] { "base-tool", "extra-tool" });
    }

    [Fact]
    public async Task Empty_Contribution_Is_A_Noop()
    {
        CompletionRequest? captured = null;
        var provider = new FakeCompletionProvider(req => { captured = req; return new CompletionResponse("ok"); });
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            SystemPrompt = "base",
            ContextProviders = new[] { new FixedContributionProvider(ContextContribution.Empty) },
        });

        await agent.AskAsync("hi");

        captured!.SystemPrompt.Should().Be("base");
        captured.History.Should().HaveCount(1);
        captured.Tools.Should().BeNull();
    }

    [Fact]
    public async Task Provider_Exception_Propagates_And_Fails_The_Turn()
    {
        var provider = new FakeCompletionProvider(_ => new CompletionResponse("ok"));
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            ContextProviders = new[] { new ThrowingContextProvider("boom") },
        });

        Func<Task> act = async () => await agent.AskAsync("hi");

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Be("boom");
    }

    [Fact]
    public async Task ContextWindowPacker_Runs_After_Providers()
    {
        CompletionRequest? capturedByProvider = null;
        CompletionRequest? capturedByChatProvider = null;

        var packer = new CapturingPacker(req =>
        {
            capturedByProvider = req;
            // Shrink the request — packer is allowed to drop turns.
            var trimmed = req.History.TakeLast(1).ToArray();
            return req with { History = trimmed };
        });

        var agent = new StatefulAiAgent(
            new FakeCompletionProvider(req => { capturedByChatProvider = req; return new CompletionResponse("ok"); }),
            new StatefulAgentOptions
            {
                ContextProviders = new[]
                {
                    new FixedContributionProvider(new ContextContribution(InjectedHistory:
                        new[] { new ChatTurn(AgentChatRole.Assistant, "ctx") })),
                },
                ContextWindowPacker = packer,
            });

        await agent.AskAsync("hi");

        // Packer sees the MERGED candidate (session history + injected context).
        capturedByProvider!.History.Should().HaveCount(2);
        // Chat provider sees the PACKED candidate (1 turn left).
        capturedByChatProvider!.History.Should().HaveCount(1);
    }

    [Fact]
    public async Task NoopContextWindowPacker_Returns_Candidate_Unchanged()
    {
        CompletionRequest? captured = null;
        var provider = new FakeCompletionProvider(req => { captured = req; return new CompletionResponse("ok"); });
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            ContextWindowPacker = NoopContextWindowPacker.Instance,
        });

        await agent.AskAsync("hello");

        captured!.History.Should().HaveCount(1);
        captured.History[0].Text.Should().Be("hello");
    }

    [Fact]
    public async Task Session_Is_Passed_To_Providers_Through_ContextInvocationContext()
    {
        IAgentSession? observedSession = null;
        AgentContext? observedContext = null;
        CompletionRequest? observedCandidate = null;

        var observer = new InvocationObserver(ctx =>
        {
            observedSession = ctx.Session;
            observedContext = ctx.AmbientContext;
            observedCandidate = ctx.Candidate;
        });

        var session = new InMemoryAgentSession("agent-x", sessionId: "sess-1");
        var agent = new StatefulAiAgent(
            new FakeCompletionProvider(_ => new CompletionResponse("ok")),
            new StatefulAgentOptions
            {
                Session = session,
                ContextProviders = new[] { observer },
            });

        await agent.AskAsync("hi");

        observedSession.Should().BeSameAs(session);
        observedContext.Should().NotBeNull();
        observedCandidate.Should().NotBeNull();
        observedCandidate!.History.Should().HaveCount(1);
        observedCandidate.History[0].Text.Should().Be("hi");
    }

    // ---- helpers ----

    private sealed class FixedContributionProvider(ContextContribution contribution) : IContextProvider
    {
        public ValueTask<ContextContribution> InvokeAsync(
            ContextInvocationContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(contribution);
    }

    private sealed class ThrowingContextProvider(string message) : IContextProvider
    {
        public ValueTask<ContextContribution> InvokeAsync(
            ContextInvocationContext context,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(message);
    }

    private sealed class CapturingPacker(Func<CompletionRequest, CompletionRequest> transform) : IContextWindowPacker
    {
        public ValueTask<CompletionRequest> PackAsync(
            CompletionRequest candidate,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(transform(candidate));
    }

    private sealed class InvocationObserver(Action<ContextInvocationContext> observe) : IContextProvider
    {
        public ValueTask<ContextContribution> InvokeAsync(
            ContextInvocationContext context,
            CancellationToken cancellationToken = default)
        {
            observe(context);
            return ValueTask.FromResult(ContextContribution.Empty);
        }
    }

    private sealed class InMemoryToolRegistry(params ITool[] tools) : IToolRegistry
    {
        public IReadOnlyList<ITool> Tools { get; } = tools;
        public ITool? GetByName(string name) => Tools.FirstOrDefault(t => t.Name == name);
    }

    private sealed class FakeTool(string name) : ITool
    {
        public string Name => name;
        public string Description => "fake";
        public JsonElement ParametersSchema { get; } = JsonSerializer.SerializeToElement(new { type = "object" });
        public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
            => Task.FromResult("{}");
    }
}
