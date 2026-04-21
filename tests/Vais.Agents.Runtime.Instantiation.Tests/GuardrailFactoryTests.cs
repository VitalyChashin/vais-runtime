// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Vais.Agents.Control;
using Vais.Agents.Core.Guardrails;
using Vais.Agents.Runtime.Instantiation.Guardrails;
using Vais.Agents.Runtime.Instantiation.ModelProviders;
using Xunit;

namespace Vais.Agents.Runtime.Instantiation.Tests;

public class GuardrailFactoryTests
{
    private static IServiceProvider BuildSp() =>
        new ServiceCollection().BuildServiceProvider();

    [Fact]
    public void LengthCapFactory_Creates_With_MaxChars_Param()
    {
        var factory = new LengthCapInputGuardrailFactory();
        var parameters = JsonDocument.Parse("""{"maxChars": 250}""").RootElement;

        var guardrail = factory.Create(parameters, BuildSp());

        guardrail.Should().BeOfType<LengthCapInputGuardrail>();
    }

    [Fact]
    public void LengthCapFactory_Missing_Param_Throws()
    {
        var factory = new LengthCapInputGuardrailFactory();
        var parameters = JsonDocument.Parse("""{}""").RootElement;

        Action act = () => factory.Create(parameters, BuildSp());

        act.Should().Throw<ManifestInstantiationException>()
            .Which.Urn.Should().Be(ManifestInstantiationUrns.GuardrailParamsInvalid);
    }

    [Fact]
    public void LengthCapFactory_WrongType_Throws()
    {
        var factory = new LengthCapInputGuardrailFactory();
        var parameters = JsonDocument.Parse("""{"maxChars": "lots"}""").RootElement;

        Action act = () => factory.Create(parameters, BuildSp());

        act.Should().Throw<ManifestInstantiationException>()
            .Which.Urn.Should().Be(ManifestInstantiationUrns.GuardrailParamsInvalid);
    }

    [Fact]
    public void RegexAllowlistInputFactory_Creates_With_Pattern()
    {
        var factory = new RegexAllowlistInputGuardrailFactory();
        var parameters = JsonDocument.Parse("""{"pattern": "^hello"}""").RootElement;

        var guardrail = factory.Create(parameters, BuildSp());

        guardrail.Should().BeOfType<RegexAllowlistInputGuardrail>();
    }

    [Fact]
    public void RegexAllowlistOutputFactory_Creates_With_Pattern()
    {
        var factory = new RegexAllowlistOutputGuardrailFactory();
        var parameters = JsonDocument.Parse("""{"pattern": "^OK"}""").RootElement;

        var guardrail = factory.Create(parameters, BuildSp());

        guardrail.Should().BeOfType<RegexAllowlistOutputGuardrail>();
    }

    [Fact]
    public void RegexDenylistInputFactory_Invalid_Regex_Throws()
    {
        var factory = new RegexDenylistInputGuardrailFactory();
        var parameters = JsonDocument.Parse("""{"pattern": "[unterminated"}""").RootElement;

        Action act = () => factory.Create(parameters, BuildSp());

        act.Should().Throw<ManifestInstantiationException>()
            .Which.Urn.Should().Be(ManifestInstantiationUrns.GuardrailParamsInvalid);
    }

    [Fact]
    public void RegexDenylistOutputFactory_Creates_With_Pattern()
    {
        var factory = new RegexDenylistOutputGuardrailFactory();
        var parameters = JsonDocument.Parse("""{"pattern": "(?i)password"}""").RootElement;

        var guardrail = factory.Create(parameters, BuildSp());

        guardrail.Should().BeOfType<RegexDenylistOutputGuardrail>();
    }

    [Fact]
    public void LlmAsJudgeFactory_Creates_With_JudgeModel_And_Prompt()
    {
        // The LlmAsJudge factory resolves ICompletionProviderPool via DI, builds the
        // judge provider through it. We register a fake pool that returns a fake provider.
        var judge = Substitute.For<ICompletionProvider>();
        judge.ProviderName.Returns("fake");

        var pool = Substitute.For<ICompletionProviderPool>();
        pool.GetAsync(Arg.Any<ModelSpec>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ICompletionProvider>(judge));

        var services = new ServiceCollection();
        services.AddSingleton(pool);
        using var sp = services.BuildServiceProvider();

        var factory = new LlmAsJudgeOutputGuardrailFactory();
        var parameters = JsonDocument.Parse("""
        {
          "judgeModel": { "provider": "openai", "id": "gpt-4o-mini" },
          "judgePrompt": "Score this: {{response}}",
          "minScore": 0.7
        }
        """).RootElement;

        var guardrail = factory.Create(parameters, sp);

        guardrail.Should().BeOfType<LlmAsJudgeOutputGuardrail>();
        pool.Received(1).GetAsync(
            Arg.Is<ModelSpec>(spec => spec.Provider == "openai" && spec.Id == "gpt-4o-mini"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void LlmAsJudgeFactory_Missing_JudgeModel_Throws()
    {
        var factory = new LlmAsJudgeOutputGuardrailFactory();
        var parameters = JsonDocument.Parse("""
        {
          "judgePrompt": "Score this: {{response}}",
          "minScore": 0.7
        }
        """).RootElement;

        Action act = () => factory.Create(parameters, BuildSp());

        act.Should().Throw<ManifestInstantiationException>()
            .Which.Urn.Should().Be(ManifestInstantiationUrns.GuardrailParamsInvalid);
    }

    // ── DI extension wiring ─────────────────────────────────────────────

    [Fact]
    public void AddBuiltinGuardrails_Registers_All_Six_Factories()
    {
        var services = new ServiceCollection();

        services.AddBuiltinGuardrails();

        using var sp = services.BuildServiceProvider();
        var registered = sp.GetServices<IGuardrailFactory>().ToArray();

        registered.Should().HaveCount(6);
        registered.Should().Contain(f => f.Name == "LengthCap" && f.Layer == GuardrailLayer.Input);
        registered.Should().Contain(f => f.Name == "RegexAllowlist" && f.Layer == GuardrailLayer.Input);
        registered.Should().Contain(f => f.Name == "RegexAllowlist" && f.Layer == GuardrailLayer.Output);
        registered.Should().Contain(f => f.Name == "RegexDenylist" && f.Layer == GuardrailLayer.Input);
        registered.Should().Contain(f => f.Name == "RegexDenylist" && f.Layer == GuardrailLayer.Output);
        registered.Should().Contain(f => f.Name == "LlmAsJudge" && f.Layer == GuardrailLayer.Output);
    }

    [Fact]
    public void AddBuiltinModelProviders_Registers_All_Three_Factories()
    {
        var services = new ServiceCollection();

        services.AddBuiltinModelProviders();

        using var sp = services.BuildServiceProvider();
        var registered = sp.GetServices<IModelProviderFactory>().Select(f => f.Provider).ToArray();

        registered.Should().Contain(new[] { "openai", "anthropic", "azure-openai" });
    }
}
