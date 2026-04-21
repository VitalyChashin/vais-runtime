// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.RegularExpressions;
using FluentAssertions;
using NSubstitute;
using Vais.Agents.Core.Guardrails;
using Xunit;

namespace Vais.Agents.Runtime.Instantiation.Tests;

public class GuardrailTests
{
    private static readonly AgentContext EmptyContext = AgentContext.Empty;

    // ── LengthCap ───────────────────────────────────────────────────────

    [Fact]
    public async Task LengthCap_Under_Cap_Passes()
    {
        var guardrail = new LengthCapInputGuardrail(100);
        var request = BuildRequest(userText: "short message");

        var outcome = await guardrail.EvaluateAsync(request, EmptyContext);

        outcome.Decision.Should().Be(GuardrailDecision.Pass);
    }

    [Fact]
    public async Task LengthCap_Over_Cap_Denies()
    {
        var guardrail = new LengthCapInputGuardrail(10);
        var request = BuildRequest(userText: "this is definitely longer than ten characters");

        var outcome = await guardrail.EvaluateAsync(request, EmptyContext);

        outcome.Decision.Should().Be(GuardrailDecision.Deny);
        outcome.Reason.Should().Contain("10-character cap");
    }

    [Fact]
    public async Task LengthCap_Empty_History_Passes()
    {
        var guardrail = new LengthCapInputGuardrail(10);
        var request = new CompletionRequest(History: Array.Empty<ChatTurn>());

        var outcome = await guardrail.EvaluateAsync(request, EmptyContext);

        outcome.Decision.Should().Be(GuardrailDecision.Pass);
    }

    // ── RegexAllowlist ──────────────────────────────────────────────────

    [Fact]
    public async Task RegexAllowlist_Input_Match_Passes()
    {
        var guardrail = new RegexAllowlistInputGuardrail(new Regex(@"^weather\s"));
        var request = BuildRequest(userText: "weather today in Paris?");

        var outcome = await guardrail.EvaluateAsync(request, EmptyContext);

        outcome.Decision.Should().Be(GuardrailDecision.Pass);
    }

    [Fact]
    public async Task RegexAllowlist_Input_NoMatch_Denies()
    {
        var guardrail = new RegexAllowlistInputGuardrail(new Regex(@"^weather\s"));
        var request = BuildRequest(userText: "tell me a joke");

        var outcome = await guardrail.EvaluateAsync(request, EmptyContext);

        outcome.Decision.Should().Be(GuardrailDecision.Deny);
    }

    [Fact]
    public async Task RegexAllowlist_Output_Match_Passes()
    {
        var guardrail = new RegexAllowlistOutputGuardrail(new Regex(@"timestamp"));
        var response = new CompletionResponse(Text: "It was 72F at timestamp 2026-04-21T10:00Z");

        var outcome = await guardrail.EvaluateAsync(response, EmptyContext);

        outcome.Decision.Should().Be(GuardrailDecision.Pass);
    }

    // ── RegexDenylist ───────────────────────────────────────────────────

    [Fact]
    public async Task RegexDenylist_Input_Match_Denies()
    {
        var guardrail = new RegexDenylistInputGuardrail(new Regex(@"\b\d{4}-\d{4}-\d{4}-\d{4}\b"));
        var request = BuildRequest(userText: "my card is 4111-1111-1111-1111 thanks");

        var outcome = await guardrail.EvaluateAsync(request, EmptyContext);

        outcome.Decision.Should().Be(GuardrailDecision.Deny);
    }

    [Fact]
    public async Task RegexDenylist_Output_Match_Denies()
    {
        var guardrail = new RegexDenylistOutputGuardrail(new Regex(@"(?i)password"));
        var response = new CompletionResponse(Text: "The password is hunter2");

        var outcome = await guardrail.EvaluateAsync(response, EmptyContext);

        outcome.Decision.Should().Be(GuardrailDecision.Deny);
    }

    // ── LLM-as-judge ────────────────────────────────────────────────────

    [Fact]
    public async Task LlmAsJudge_AboveThreshold_Passes()
    {
        var judge = Substitute.For<ICompletionProvider>();
        judge.ProviderName.Returns("fake");
        judge.CompleteAsync(Arg.Any<CompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CompletionResponse(Text: "0.9"));

        var guardrail = new LlmAsJudgeOutputGuardrail(judge, "Judge this: {{response}}", minScore: 0.7);
        var response = new CompletionResponse(Text: "Helpful answer about the weather.");

        var outcome = await guardrail.EvaluateAsync(response, EmptyContext);

        outcome.Decision.Should().Be(GuardrailDecision.Pass);
    }

    [Fact]
    public async Task LlmAsJudge_BelowThreshold_Denies()
    {
        var judge = Substitute.For<ICompletionProvider>();
        judge.ProviderName.Returns("fake");
        judge.CompleteAsync(Arg.Any<CompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CompletionResponse(Text: "0.3"));

        var guardrail = new LlmAsJudgeOutputGuardrail(judge, "Judge this: {{response}}", minScore: 0.7);
        var response = new CompletionResponse(Text: "Uncertain answer.");

        var outcome = await guardrail.EvaluateAsync(response, EmptyContext);

        outcome.Decision.Should().Be(GuardrailDecision.Deny);
        outcome.Reason.Should().Contain("0.30");
    }

    [Fact]
    public async Task LlmAsJudge_Unparseable_Denies()
    {
        var judge = Substitute.For<ICompletionProvider>();
        judge.ProviderName.Returns("fake");
        judge.CompleteAsync(Arg.Any<CompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CompletionResponse(Text: "I'm not sure how to score this."));

        var guardrail = new LlmAsJudgeOutputGuardrail(judge, "Judge: {{response}}", minScore: 0.7);
        var response = new CompletionResponse(Text: "Some output.");

        var outcome = await guardrail.EvaluateAsync(response, EmptyContext);

        outcome.Decision.Should().Be(GuardrailDecision.Deny);
        outcome.Reason.Should().Contain("could not parse");
    }

    [Fact]
    public async Task LlmAsJudge_Substitutes_Response_Placeholder()
    {
        var judge = Substitute.For<ICompletionProvider>();
        judge.ProviderName.Returns("fake");
        judge.CompleteAsync(Arg.Any<CompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CompletionResponse(Text: "1.0"));

        var guardrail = new LlmAsJudgeOutputGuardrail(judge, "The response is: {{response}}. Score it.", minScore: 0.5);
        var response = new CompletionResponse(Text: "Hello world");

        await guardrail.EvaluateAsync(response, EmptyContext);

        await judge.Received().CompleteAsync(
            Arg.Is<CompletionRequest>(r => r.SystemPrompt == "The response is: Hello world. Score it."),
            Arg.Any<CancellationToken>());
    }

    // ── Construction guards ─────────────────────────────────────────────

    [Fact]
    public void LengthCap_Negative_Throws()
    {
        Action act = () => new LengthCapInputGuardrail(0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void LlmAsJudge_OutOfRange_Score_Throws()
    {
        var judge = Substitute.For<ICompletionProvider>();
        Action act = () => new LlmAsJudgeOutputGuardrail(judge, "prompt {{response}}", minScore: 1.5);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static CompletionRequest BuildRequest(string userText) =>
        new(History: new[] { new ChatTurn(AgentChatRole.User, userText) });
}
