// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Vais.Agents.Control;
using Vais.Agents.Core;
using Vais.Agents.Runtime.Instantiation;
using Xunit;

namespace Vais.Agents.Runtime.Host.Tests;

/// <summary>
/// v0.17 Pillar B end-to-end integration. Exercises the full composition-root
/// wiring (sans Orleans silo startup) to prove that:
///
/// 1. <c>CompositionRoot.ConfigureServices</c> wires the translator, model
///    providers, guardrails, and <c>ConfigureAgentGrains</c> factory correctly.
/// 2. A registered manifest flows through the translator at grain-activation
///    time (simulated by invoking the registered <c>Func&lt;string, CancellationToken, ValueTask&lt;StatefulAgentOptions&gt;&gt;</c>).
/// 3. The resulting <see cref="StatefulAgentOptions.CompletionProvider"/> is
///    set to the factory-produced provider.
/// 4. A <see cref="StatefulAiAgent"/> constructed from those options runs
///    end-to-end against the supplied provider.
///
/// Shortcuts taken to avoid Orleans silo startup in unit tests:
/// - <c>IAgentRegistry</c> is overridden with <see cref="InMemoryAgentRegistry"/>
///   after <see cref="CompositionRoot.ConfigureServices"/> runs — the Orleans
///   registry's grain-backed behaviour is covered by CrossHostTests.
/// - Built-in model providers are replaced with a <c>fake</c> factory so no
///   outbound network calls happen.
/// </summary>
public class ManifestInstantiationIntegrationTests
{
    [Fact]
    public async Task Apply_Invoke_Returns_Mocked_Provider_Response()
    {
        var services = BuildServicesWithFakeProvider();

        using var sp = services.BuildServiceProvider();

        // Arrange: persist a declarative manifest under id "weather".
        var registry = (InMemoryAgentRegistry)sp.GetRequiredService<IAgentRegistry>();
        registry.Register(BuildWeatherManifest());

        // Simulate grain activation: call the ConfigureAgentGrains-registered factory
        // with the agent id, just as AiAgentGrain.OnActivateAsync would.
        var factory = sp.GetRequiredService<Func<string, CancellationToken, ValueTask<StatefulAgentOptions>>>();
        var options = await factory("weather", CancellationToken.None);

        // Assert: translator produced options with provider + system prompt + budget.
        options.AgentName.Should().Be("weather");
        options.CompletionProvider.Should().NotBeNull();
        options.SystemPrompt.Should().Be("You help with weather questions.");
        options.Budget.Should().NotBeNull();
        options.Budget!.MaxTurns.Should().Be(5);

        // Act: construct the agent exactly as AiAgentGrain would + invoke.
        var agent = new StatefulAiAgent(options.CompletionProvider!, options);
        var reply = await agent.AskAsync("What's the weather in Tokyo?");

        // Assert: agent ran end-to-end against the fake provider.
        reply.Should().Be("fake-response: What's the weather in Tokyo?");
    }

    [Fact]
    public async Task Update_Invalidates_Translator_Cache_So_Next_Activation_Picks_New_Prompt()
    {
        var services = BuildServicesWithFakeProvider();

        using var sp = services.BuildServiceProvider();
        var registry = (InMemoryAgentRegistry)sp.GetRequiredService<IAgentRegistry>();
        var translator = sp.GetRequiredService<IAgentManifestTranslator>();
        var factory = sp.GetRequiredService<Func<string, CancellationToken, ValueTask<StatefulAgentOptions>>>();

        var v1 = BuildWeatherManifest() with { SystemPrompt = new SystemPromptSpec(Inline: "v1 prompt") };
        registry.Register(v1);

        var optsV1 = await factory("weather", CancellationToken.None);
        optsV1.SystemPrompt.Should().Be("v1 prompt");

        // Update flow: re-register with new prompt then invalidate the translator cache.
        // This is what AgentLifecycleManager.UpdateAsync is expected to do in v0.17 PR 4.
        var v2 = v1 with
        {
            Version = "2.0",
            SystemPrompt = new SystemPromptSpec(Inline: "v2 prompt")
        };
        registry.Register(v2);
        var invalidated = await translator.InvalidateAsync("weather");

        invalidated.Should().BeTrue(because: "the translator cached v1's options; InvalidateAsync must drop the entry.");

        // Next grain activation sees the new manifest.
        var optsV2 = await factory("weather", CancellationToken.None);
        optsV2.SystemPrompt.Should().Be("v2 prompt");
    }

    private static ServiceCollection BuildServicesWithFakeProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IGrainFactory>());
        services.AddSingleton(Substitute.For<IClusterClient>());
        services.AddLogging();

        // Run the real composition root first so we exercise the same wiring
        // production uses.
        CompositionRoot.ConfigureServices(services, new RuntimeOptions());

        // Override IAgentRegistry with an in-memory stand-in (OrleansAgentRegistry
        // needs a working IGrainFactory — CrossHostTests cover that path).
        var inMemoryRegistry = new InMemoryAgentRegistry();
        services.AddSingleton<IAgentRegistry>(inMemoryRegistry);

        // Replace the three built-in IModelProviderFactory registrations with a
        // fake that returns a deterministic ICompletionProvider. The fake's
        // Provider = "fake" matches the manifest's ModelSpec.Provider below.
        services.RemoveAll<IModelProviderFactory>();
        services.AddSingleton<IModelProviderFactory, FakeModelProviderFactory>();

        // Ensure the pool + translator rebuild against the fake factories. We
        // registered them via TryAddSingleton so removal + re-add of the IModelProviderFactory
        // entries is enough; the pool captures the enumerable at construction time.

        return services;
    }

    private static AgentManifest BuildWeatherManifest() => new(
        Id: "weather",
        Version: "1.0",
        Handler: new AgentHandlerRef("declarative"),
        Protocols: Array.Empty<ProtocolBinding>(),
        Tools: Array.Empty<ToolRef>())
    {
        Model = new ModelSpec(Provider: "fake", Id: "fake-gpt-4", ApiKeyRef: "secret://env/TEST"),
        SystemPrompt = new SystemPromptSpec(Inline: "You help with weather questions."),
        Budget = new RunBudget(MaxTurns: 5),
    };

    private sealed class FakeModelProviderFactory : IModelProviderFactory
    {
        public string Provider => "fake";

        public ValueTask<ICompletionProvider> CreateAsync(
            ModelSpec spec,
            ISecretResolver secrets,
            CancellationToken cancellationToken = default) =>
            new(new FakeCompletionProvider());
    }

    private sealed class FakeCompletionProvider : ICompletionProvider
    {
        public string ProviderName => "fake";

        public Task<CompletionResponse> CompleteAsync(
            CompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            var lastUser = request.History.LastOrDefault(t => t.Role == AgentChatRole.User);
            var text = lastUser?.Text ?? "(empty)";
            return Task.FromResult(new CompletionResponse(
                Text: $"fake-response: {text}",
                ModelId: "fake-gpt-4",
                PromptTokens: 10,
                CompletionTokens: 5));
        }
    }
}
