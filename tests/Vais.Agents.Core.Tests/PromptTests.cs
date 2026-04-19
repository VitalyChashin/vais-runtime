// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests;

public sealed class FormatStringPromptTemplateTests
{
    [Fact]
    public async Task Substitutes_Known_Variables()
    {
        var vars = new Dictionary<string, object?> { ["name"] = "Ada", ["role"] = "navigator" };
        var result = await FormatStringPromptTemplate.Instance.RenderAsync("Hello {name}, you are the {role}.", vars);
        result.Should().Be("Hello Ada, you are the navigator.");
    }

    [Fact]
    public async Task Leaves_Unknown_Variables_As_Literal_Tokens()
    {
        var vars = new Dictionary<string, object?> { ["known"] = "yes" };
        var result = await FormatStringPromptTemplate.Instance.RenderAsync("{known} / {missing}", vars);
        result.Should().Be("yes / {missing}");
    }

    [Fact]
    public async Task Null_Variable_Value_Becomes_Empty_String()
    {
        var vars = new Dictionary<string, object?> { ["x"] = null };
        var result = await FormatStringPromptTemplate.Instance.RenderAsync("a{x}b", vars);
        result.Should().Be("ab");
    }

    [Fact]
    public async Task Template_With_No_Braces_Is_Returned_Unchanged()
    {
        var result = await FormatStringPromptTemplate.Instance.RenderAsync(
            "plain text, no substitutions",
            new Dictionary<string, object?>());
        result.Should().Be("plain text, no substitutions");
    }

    [Fact]
    public async Task Unmatched_Opening_Brace_Is_Emitted_Verbatim()
    {
        var vars = new Dictionary<string, object?> { ["k"] = "v" };
        var result = await FormatStringPromptTemplate.Instance.RenderAsync("{k and then {no close", vars);
        result.Should().Be("{k and then {no close");
    }

    [Fact]
    public async Task Calls_ToString_On_Non_String_Values()
    {
        var vars = new Dictionary<string, object?> { ["n"] = 42 };
        var result = await FormatStringPromptTemplate.Instance.RenderAsync("count={n}", vars);
        result.Should().Be("count=42");
    }
}

public sealed class AggregatingSystemPromptComposerTests
{
    [Fact]
    public async Task No_Contributors_Returns_Null()
    {
        var composer = new AggregatingSystemPromptComposer(Array.Empty<ISystemPromptContributor>());
        var result = await composer.ComposeAsync(AgentContext.Empty);
        result.Should().BeNull();
    }

    [Fact]
    public async Task Orders_By_Priority_Ascending()
    {
        var composer = new AggregatingSystemPromptComposer(new ISystemPromptContributor[]
        {
            new Fixed(priority: 20, text: "second"),
            new Fixed(priority: 10, text: "first"),
            new Fixed(priority: 30, text: "third"),
        });

        var result = await composer.ComposeAsync(AgentContext.Empty);

        result.Should().Be("first\n\nsecond\n\nthird");
    }

    [Fact]
    public async Task Skips_Null_And_Empty_Contributions()
    {
        var composer = new AggregatingSystemPromptComposer(new ISystemPromptContributor[]
        {
            new Fixed(priority: 1, text: "kept"),
            new Fixed(priority: 2, text: null),
            new Fixed(priority: 3, text: ""),
            new Fixed(priority: 4, text: "also-kept"),
        });

        var result = await composer.ComposeAsync(AgentContext.Empty);

        result.Should().Be("kept\n\nalso-kept");
    }

    [Fact]
    public async Task All_Null_Or_Empty_Returns_Null()
    {
        var composer = new AggregatingSystemPromptComposer(new ISystemPromptContributor[]
        {
            new Fixed(priority: 1, text: null),
            new Fixed(priority: 2, text: ""),
        });

        var result = await composer.ComposeAsync(AgentContext.Empty);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Contributors_See_AgentContext()
    {
        AgentContext? observed = null;
        var contributor = new Observing(ctx => { observed = ctx; return "x"; });
        var composer = new AggregatingSystemPromptComposer(new[] { contributor });

        var ctx = new AgentContext(UserId: "u", TenantId: "t");
        await composer.ComposeAsync(ctx);

        observed.Should().Be(ctx);
    }

    private sealed class Fixed(int priority, string? text) : ISystemPromptContributor
    {
        public int Priority => priority;
        public ValueTask<string?> ContributeAsync(AgentContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(text);
    }

    private sealed class Observing(Func<AgentContext, string?> capture) : ISystemPromptContributor
    {
        public int Priority => 0;
        public ValueTask<string?> ContributeAsync(AgentContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(capture(context));
    }
}

public sealed class StatefulAiAgentComposerIntegrationTests
{
    [Fact]
    public async Task Composer_Result_Becomes_Base_SystemPrompt_When_Set()
    {
        CompletionRequest? captured = null;
        var provider = new FakeCompletionProvider(req => { captured = req; return new CompletionResponse("ok"); });
        var composer = new FixedComposer("composed-base");

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            SystemPrompt = "this-is-ignored",
            SystemPromptComposer = composer,
        });

        await agent.AskAsync("hi");

        captured!.SystemPrompt.Should().Be("composed-base");
    }

    [Fact]
    public async Task SystemPrompt_String_Is_Used_When_No_Composer()
    {
        CompletionRequest? captured = null;
        var provider = new FakeCompletionProvider(req => { captured = req; return new CompletionResponse("ok"); });

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            SystemPrompt = "plain-string-base",
        });

        await agent.AskAsync("hi");

        captured!.SystemPrompt.Should().Be("plain-string-base");
    }

    [Fact]
    public async Task Context_Provider_Addendum_Concatenates_On_Composer_Output()
    {
        CompletionRequest? captured = null;
        var provider = new FakeCompletionProvider(req => { captured = req; return new CompletionResponse("ok"); });
        var composer = new FixedComposer("composed-base");

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            SystemPromptComposer = composer,
            ContextProviders = new[]
            {
                new FixedAddendumProvider("retrieved-context"),
            },
        });

        await agent.AskAsync("hi");

        captured!.SystemPrompt.Should().Be("composed-base\n\nretrieved-context");
    }

    [Fact]
    public async Task Composer_Returning_Null_Produces_Null_Base_With_Addendum_Only()
    {
        CompletionRequest? captured = null;
        var provider = new FakeCompletionProvider(req => { captured = req; return new CompletionResponse("ok"); });

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            SystemPrompt = "ignored-because-composer-set",
            SystemPromptComposer = new FixedComposer(null),
            ContextProviders = new[]
            {
                new FixedAddendumProvider("addendum-only"),
            },
        });

        await agent.AskAsync("hi");

        captured!.SystemPrompt.Should().Be("addendum-only");
    }

    private sealed class FixedComposer(string? result) : ISystemPromptComposer
    {
        public ValueTask<string?> ComposeAsync(AgentContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(result);
    }

    private sealed class FixedAddendumProvider(string addendum) : IContextProvider
    {
        public ValueTask<ContextContribution> InvokeAsync(ContextInvocationContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new ContextContribution(SystemPromptAddendum: addendum));
    }
}
