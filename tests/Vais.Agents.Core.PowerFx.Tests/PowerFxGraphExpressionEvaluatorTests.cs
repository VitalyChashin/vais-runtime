// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.PowerFx.Tests;

public sealed class PowerFxGraphExpressionEvaluatorTests
{
    private readonly PowerFxGraphExpressionEvaluator _sut = new();

    private static IReadOnlyDictionary<string, JsonElement> State(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    // ---- basic true/false ----

    [Fact]
    public async Task Literal_True_Returns_True()
    {
        var result = await _sut.EvaluatePredicateAsync("true", State("{}"));
        result.Should().BeTrue();
    }

    [Fact]
    public async Task Literal_False_Returns_False()
    {
        var result = await _sut.EvaluatePredicateAsync("false", State("{}"));
        result.Should().BeFalse();
    }

    // ---- = prefix stripping ----

    [Fact]
    public async Task EqualSign_Prefix_Is_Stripped()
    {
        var result = await _sut.EvaluatePredicateAsync("=true", State("{}"));
        result.Should().BeTrue();
    }

    // ---- Local.* state access ----

    [Fact]
    public async Task Local_StringKey_IsBlank_FalseWhenPresent()
    {
        var state = State("""{"research_plan": "some plan"}""");
        var result = await _sut.EvaluatePredicateAsync("=Not(IsBlank(Local.research_plan))", state);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task Local_StringKey_IsBlank_TrueWhenEmpty()
    {
        // PowerFx only sees fields present in the record — missing keys are schema errors.
        // IsBlank returns true for empty strings.
        var state = State("""{"research_plan": ""}""");
        var result = await _sut.EvaluatePredicateAsync("=IsBlank(Local.research_plan)", state);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task Local_NumberKey_Comparison()
    {
        // Use integer literals to avoid PowerFx decimal-literal locale ambiguity.
        var state = State("""{"retry_count": 5}""");
        var result = await _sut.EvaluatePredicateAsync("=Local.retry_count > 3", state);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task Local_BoolKey_DirectTest()
    {
        var state = State("""{"approved": true}""");
        var result = await _sut.EvaluatePredicateAsync("=Local.approved", state);
        result.Should().BeTrue();
    }

    // ---- hyphen → underscore normalisation ----

    [Fact]
    public async Task Hyphenated_Key_Is_Normalised_To_Underscore()
    {
        var state = State("""{"research-plan": "step 1"}""");
        var result = await _sut.EvaluatePredicateAsync("=Not(IsBlank(Local.research_plan))", state);
        result.Should().BeTrue();
    }

    // ---- Local.lastMessage shortcut ----

    [Fact]
    public async Task LastMessage_Text_Accessible()
    {
        var state = State("""{"messages": [{"role":"user","text":"hello"}]}""");
        var result = await _sut.EvaluatePredicateAsync(
            """=Local.lastMessage.text = "hello" """, state);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task LastMessage_ReturnsLast_WhenMultiple()
    {
        var state = State("""{"messages": [{"text":"first"},{"text":"last"}]}""");
        var result = await _sut.EvaluatePredicateAsync(
            """=Local.lastMessage.text = "last" """, state);
        result.Should().BeTrue();
    }

    // ---- error cases ----

    [Fact]
    public async Task NonBoolean_Result_Throws()
    {
        var act = () => _sut.EvaluatePredicateAsync("=42", State("{}")).AsTask();
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*non-boolean*");
    }

    [Fact]
    public async Task EvalError_Throws_WithMessage()
    {
        // Calling an undefined function is a PowerFx error.
        var act = () => _sut.EvaluatePredicateAsync("=UndefinedFn123()", State("{}")).AsTask();
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
