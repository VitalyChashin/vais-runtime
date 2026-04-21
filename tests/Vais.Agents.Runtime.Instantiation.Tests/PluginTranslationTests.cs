// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using NSubstitute;
using Vais.Agents.Control;
using Xunit;

namespace Vais.Agents.Runtime.Instantiation.Tests;

/// <summary>
/// v0.18 Pillar C PR 2 — plugin-branch coverage for <see cref="AgentManifestTranslator"/>.
/// Verifies the plugin lookup path takes precedence over the v0.17 declarative path,
/// that factory throws surface as <c>plugin-factory-throw</c>, and that
/// apply-time warnings land on <see cref="IManifestApplyDiagnosticsSink"/> when
/// both a plugin handler AND declarative Model fields are set.
/// </summary>
public class PluginTranslationTests
{
    private const string AgentId = "plugin-agent";
    private const string Version = "1.0";
    private const string HandlerTypeName = "Vais.Agents.Samples.PluginAgent";

    [Fact]
    public async Task TranslateAsync_Plugin_Match_Returns_Agent_From_Factory()
    {
        var pluginAgent = new FakeAiAgent();
        var manifest = BuildManifestForPlugin(HandlerTypeName);

        var fixture = new TranslatorFixture()
            .WithManifest(manifest)
            .WithPluginHandler(HandlerTypeName, (_, _) => pluginAgent);

        var options = await fixture.Translator.TranslateAsync(AgentId);

        options.Agent.Should().BeSameAs(pluginAgent);
        options.AgentName.Should().Be(AgentId);
        options.CompletionProvider.Should().BeNull(because: "plugin-supplied agents bring their own provider");
    }

    [Fact]
    public async Task TranslateAsync_Plugin_Match_Is_Cached_Across_Calls()
    {
        var pluginAgent = new FakeAiAgent();
        var manifest = BuildManifestForPlugin(HandlerTypeName);

        var fixture = new TranslatorFixture()
            .WithManifest(manifest)
            .WithPluginHandler(HandlerTypeName, (_, _) => pluginAgent);

        var first = await fixture.Translator.TranslateAsync(AgentId);
        var second = await fixture.Translator.TranslateAsync(AgentId);

        second.Should().BeSameAs(first);
    }

    [Fact]
    public async Task TranslateAsync_No_Plugin_Match_Falls_Through_To_Declarative()
    {
        // Handler TypeName is "declarative" (no factory registered) but Model is set → v0.17 path wins.
        var manifest = new AgentManifest(
            Id: AgentId,
            Version: Version,
            Handler: new AgentHandlerRef("declarative"),
            Protocols: Array.Empty<ProtocolBinding>(),
            Tools: Array.Empty<ToolRef>())
        {
            Model = new ModelSpec(Provider: "openai", Id: "gpt-4o"),
            SystemPrompt = new SystemPromptSpec(Inline: "hi"),
        };

        var fixture = new TranslatorFixture()
            .WithManifest(manifest)
            .WithProvider("openai")
            .WithPluginHandler("some.other.type", (_, _) => new FakeAiAgent());

        var options = await fixture.Translator.TranslateAsync(AgentId);

        options.Agent.Should().BeNull(because: "plugin registry didn't match; declarative path populated CompletionProvider instead");
        options.CompletionProvider.Should().NotBeNull();
        options.SystemPrompt.Should().Be("hi");
    }

    [Fact]
    public async Task TranslateAsync_Plugin_Match_Wins_Over_Declarative_When_Both_Set()
    {
        var pluginAgent = new FakeAiAgent();
        // Manifest has BOTH a plugin-matching handler AND a Model — plugin must win,
        // CompletionProvider must stay null.
        var manifest = new AgentManifest(
            Id: AgentId,
            Version: Version,
            Handler: new AgentHandlerRef(HandlerTypeName),
            Protocols: Array.Empty<ProtocolBinding>(),
            Tools: Array.Empty<ToolRef>())
        {
            Model = new ModelSpec(Provider: "openai", Id: "gpt-4o"),
            SystemPrompt = new SystemPromptSpec(Inline: "ignored"),
        };

        var fixture = new TranslatorFixture()
            .WithManifest(manifest)
            .WithProvider("openai")
            .WithPluginHandler(HandlerTypeName, (_, _) => pluginAgent);

        var options = await fixture.Translator.TranslateAsync(AgentId);

        options.Agent.Should().BeSameAs(pluginAgent);
        options.CompletionProvider.Should().BeNull();
        options.SystemPrompt.Should().BeNull(because: "plugin wins; declarative SystemPrompt is ignored");
    }

    [Fact]
    public async Task TranslateAsync_Plugin_And_Declarative_Both_Set_Records_Warning()
    {
        var pluginAgent = new FakeAiAgent();
        var sink = Substitute.For<IManifestApplyDiagnosticsSink>();
        var manifest = new AgentManifest(
            Id: AgentId,
            Version: Version,
            Handler: new AgentHandlerRef(HandlerTypeName),
            Protocols: Array.Empty<ProtocolBinding>(),
            Tools: Array.Empty<ToolRef>())
        {
            Model = new ModelSpec(Provider: "openai", Id: "gpt-4o"),
        };

        var fixture = new TranslatorFixture()
            .WithManifest(manifest)
            .WithProvider("openai")
            .WithPluginHandler(HandlerTypeName, (_, _) => pluginAgent)
            .WithDiagnosticsSink(sink);

        _ = await fixture.Translator.TranslateAsync(AgentId);

        sink.Received(1).Record(
            AgentId,
            ManifestInstantiationUrns.HandlerAndDeclarativeFieldsBothSet,
            Arg.Is<string>(s => s.Contains(HandlerTypeName)));
    }

    [Fact]
    public async Task TranslateAsync_Plugin_Match_Without_Declarative_Does_Not_Warn()
    {
        var sink = Substitute.For<IManifestApplyDiagnosticsSink>();
        var manifest = BuildManifestForPlugin(HandlerTypeName);

        var fixture = new TranslatorFixture()
            .WithManifest(manifest)
            .WithPluginHandler(HandlerTypeName, (_, _) => new FakeAiAgent())
            .WithDiagnosticsSink(sink);

        _ = await fixture.Translator.TranslateAsync(AgentId);

        sink.DidNotReceive().Record(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task TranslateAsync_Plugin_Factory_Throws_Surfaces_PluginFactoryThrow()
    {
        var manifest = BuildManifestForPlugin(HandlerTypeName);
        var fixture = new TranslatorFixture()
            .WithManifest(manifest)
            .WithPluginHandler(HandlerTypeName, (_, _) => throw new InvalidOperationException("boom"));

        var act = async () => await fixture.Translator.TranslateAsync(AgentId);

        var ex = await act.Should().ThrowAsync<ManifestInstantiationException>();
        ex.Which.Urn.Should().Be(ManifestInstantiationUrns.PluginFactoryThrow);
        ex.Which.InnerException.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("boom");
    }

    [Fact]
    public async Task TranslateAsync_Plugin_Factory_Throws_Does_Not_Cache_Options()
    {
        var manifest = BuildManifestForPlugin(HandlerTypeName);
        var callCount = 0;
        var fixture = new TranslatorFixture()
            .WithManifest(manifest)
            .WithPluginHandler(HandlerTypeName, (_, _) =>
            {
                callCount++;
                throw new InvalidOperationException("boom");
            });

        var translator = fixture.Translator;
        await Assert.ThrowsAsync<ManifestInstantiationException>(() => translator.TranslateAsync(AgentId).AsTask());
        await Assert.ThrowsAsync<ManifestInstantiationException>(() => translator.TranslateAsync(AgentId).AsTask());

        callCount.Should().Be(2, because: "a factory throw must not cache a partial result — second call retries the factory");
    }

    [Fact]
    public async Task TranslateAsync_No_Plugin_Registry_And_Null_Model_Still_Throws_HandlerNotLoaded()
    {
        // No plugin registry wired at all — unknown handler + no Model must surface HandlerNotLoaded.
        var manifest = new AgentManifest(
            Id: AgentId,
            Version: Version,
            Handler: new AgentHandlerRef(HandlerTypeName),
            Protocols: Array.Empty<ProtocolBinding>(),
            Tools: Array.Empty<ToolRef>());

        var fixture = new TranslatorFixture().WithManifest(manifest);

        var act = async () => await fixture.Translator.TranslateAsync(AgentId);

        var ex = await act.Should().ThrowAsync<ManifestInstantiationException>();
        ex.Which.Urn.Should().Be(ManifestInstantiationUrns.HandlerNotLoaded);
        ex.Which.Message.Should().Contain(HandlerTypeName);
    }

    [Fact]
    public async Task TranslateAsync_Plugin_Registry_Unknown_Handler_Falls_Through_To_HandlerNotLoaded()
    {
        // Plugin registry IS wired but does not know this handler, Model is null.
        var manifest = new AgentManifest(
            Id: AgentId,
            Version: Version,
            Handler: new AgentHandlerRef(HandlerTypeName),
            Protocols: Array.Empty<ProtocolBinding>(),
            Tools: Array.Empty<ToolRef>());

        var fixture = new TranslatorFixture()
            .WithManifest(manifest)
            .WithPluginHandler("completely.different", (_, _) => new FakeAiAgent());

        var act = async () => await fixture.Translator.TranslateAsync(AgentId);

        var ex = await act.Should().ThrowAsync<ManifestInstantiationException>();
        ex.Which.Urn.Should().Be(ManifestInstantiationUrns.HandlerNotLoaded);
    }

    [Fact]
    public async Task TranslateAsync_Plugin_Factory_Receives_Manifest_And_ServiceProvider()
    {
        AgentManifest? capturedManifest = null;
        IServiceProvider? capturedServices = null;
        var manifest = BuildManifestForPlugin(HandlerTypeName);

        var fixture = new TranslatorFixture()
            .WithManifest(manifest)
            .WithPluginHandler(HandlerTypeName, (m, sp) =>
            {
                capturedManifest = m;
                capturedServices = sp;
                return new FakeAiAgent();
            });

        _ = await fixture.Translator.TranslateAsync(AgentId);

        capturedManifest.Should().BeSameAs(manifest);
        capturedServices.Should().NotBeNull();
    }

    [Fact]
    public async Task TranslateAsync_Plugin_Match_Preserves_Manifest_Budget()
    {
        var budget = new RunBudget(MaxTurns: 7, MaxDuration: TimeSpan.FromMinutes(2));
        var manifest = new AgentManifest(
            Id: AgentId,
            Version: Version,
            Handler: new AgentHandlerRef(HandlerTypeName),
            Protocols: Array.Empty<ProtocolBinding>(),
            Tools: Array.Empty<ToolRef>())
        {
            Budget = budget,
        };

        var fixture = new TranslatorFixture()
            .WithManifest(manifest)
            .WithPluginHandler(HandlerTypeName, (_, _) => new FakeAiAgent());

        var options = await fixture.Translator.TranslateAsync(AgentId);

        options.Budget.Should().Be(budget);
    }

    [Fact]
    public async Task TranslateAsync_Plugin_Match_Cancellation_Propagates_Without_Wrapping()
    {
        var manifest = BuildManifestForPlugin(HandlerTypeName);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var fixture = new TranslatorFixture()
            .WithManifest(manifest)
            .WithPluginHandler(HandlerTypeName, (_, _) => throw new OperationCanceledException());

        var act = async () => await fixture.Translator.TranslateAsync(AgentId, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            because: "OperationCanceledException must not be wrapped in ManifestInstantiationException");
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static AgentManifest BuildManifestForPlugin(string handlerTypeName) =>
        new(
            Id: AgentId,
            Version: Version,
            Handler: new AgentHandlerRef(handlerTypeName),
            Protocols: Array.Empty<ProtocolBinding>(),
            Tools: Array.Empty<ToolRef>());

    private sealed class FakeAiAgent : IAiAgent
    {
        public string? SystemPrompt { get; set; }

        public IAgentSession Session { get; } = new FakeSession();

        public IReadOnlyList<ChatTurn> History => Session.History;

        public Task<string> AskAsync(string userMessage, CancellationToken cancellationToken = default)
            => Task.FromResult("reply");

        public void Reset() => ((FakeSession)Session).Clear();
    }

    private sealed class FakeSession : IAgentSession
    {
        private readonly List<ChatTurn> _history = new();

        public string SessionId => "session-1";
        public string AgentId => "plugin-agent";
        public IReadOnlyList<ChatTurn> History => _history;

        public ValueTask AppendAsync(ChatTurn turn, CancellationToken cancellationToken = default)
        {
            _history.Add(turn);
            return ValueTask.CompletedTask;
        }

        public ValueTask ResetAsync(CancellationToken cancellationToken = default)
        {
            _history.Clear();
            return ValueTask.CompletedTask;
        }

        public void Clear() => _history.Clear();
    }
}
