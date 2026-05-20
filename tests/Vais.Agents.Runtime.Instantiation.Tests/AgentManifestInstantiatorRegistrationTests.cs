// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Vais.Agents.Control;
using Vais.Agents.Runtime.Plugins;
using Xunit;

namespace Vais.Agents.Runtime.Instantiation.Tests;

/// <summary>
/// M2 (A4) guard: <see cref="AgentManifestInstantiatorServiceCollectionExtensions.AddAgentManifestInstantiator"/>
/// type-registers <c>ICompletionProviderPool</c> and <c>IAgentManifestTranslator</c> (rather than via
/// factory delegates) so the container constructs them and <c>ValidateOnBuild</c> validates their
/// required ctor dependencies at startup. These tests prove the type registration resolves with only
/// the required services present — i.e. MS.DI honors the optional <c>= null</c> ctor parameters for
/// every unregistered optional dependency.
/// </summary>
public sealed class AgentManifestInstantiatorRegistrationTests
{
    private static ServiceCollection BuildMinimalRequired()
    {
        var services = new ServiceCollection();
        // The only services the translator + pool require (ArgumentNullException-guarded ctor params).
        // Logging is intentionally omitted — ILogger<T> is an optional ctor param defaulted to null.
        services.AddSingleton<IAgentRegistry>(Substitute.For<IAgentRegistry>());
        services.AddSingleton<ISecretResolver>(Substitute.For<ISecretResolver>());
        return services;
    }

    [Fact]
    public void Translator_And_Pool_Resolve_With_Only_Required_Deps_Under_ValidateOnBuild()
    {
        var services = BuildMinimalRequired();

        services.AddAgentManifestInstantiator();

        // ValidateOnBuild would throw here if any unregistered optional ctor parameter
        // (IStaticToolRegistry, IPluginHandlerRegistry, the gateway registries/factories, …)
        // were treated as required rather than defaulted to null.
        using var sp = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        sp.GetRequiredService<IAgentManifestTranslator>().Should().NotBeNull(
            because: "type-registered AgentManifestTranslator must construct from required deps with optionals defaulted to null.");
        sp.GetRequiredService<ICompletionProviderPool>().Should().NotBeNull(
            because: "type-registered CompletionProviderPool must construct from IEnumerable<IModelProviderFactory> (empty) + ISecretResolver.");
    }

    [Fact]
    public void Translator_Singleton_Is_Shared_With_Invalidator_Alias()
    {
        var services = BuildMinimalRequired();
        services.AddAgentManifestInstantiator();

        using var sp = services.BuildServiceProvider();

        var translator = sp.GetRequiredService<IAgentManifestTranslator>();
        var invalidator = sp.GetRequiredService<IAgentManifestInvalidator>();

        invalidator.Should().BeSameAs(translator,
            because: "the IAgentManifestInvalidator alias must forward to the same translator singleton so cache eviction flows through one instance.");
    }

    [Fact]
    public async Task Translator_Uses_PluginRegistry_Registered_After_Instantiator()
    {
        // M4 order-independence (the honest replacement for the deleted "registered before
        // translator" host tests): registering IPluginHandlerRegistry AFTER AddAgentManifestInstantiator
        // must still route a plugin-handler manifest to the plugin. The translator resolves the
        // registry at activation, so registration order is irrelevant. This asserts the actual
        // routing behavior, not merely that two services resolve from the same provider.
        const string agentId = "plugin-agent";
        const string handlerType = "Some.Plugin.Type";

        var pluginAgent = Substitute.For<IAiAgent>();

        var manifest = new AgentManifest(
            Id: agentId,
            Version: "1.0",
            Handler: new AgentHandlerRef(handlerType),
            Protocols: Array.Empty<ProtocolBinding>(),
            Tools: Array.Empty<ToolRef>());

        var agentRegistry = Substitute.For<IAgentRegistry>();
        agentRegistry.GetAsync(agentId, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<AgentManifest?>(manifest));

        var handlerFactory = Substitute.For<IAgentHandlerFactory>();
        handlerFactory.HandlerTypeName.Returns(handlerType);
        handlerFactory.CreateAsync(Arg.Any<AgentManifest>(), Arg.Any<IServiceProvider>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IAiAgent>(pluginAgent));

        var pluginRegistry = Substitute.For<IPluginHandlerRegistry>();
        pluginRegistry.TryGet(handlerType, out Arg.Any<IAgentHandlerFactory?>())
            .Returns(call => { call[1] = handlerFactory; return true; });

        var services = new ServiceCollection();
        services.AddSingleton<ISecretResolver>(Substitute.For<ISecretResolver>());
        services.AddSingleton(agentRegistry);
        services.AddAgentManifestInstantiator(); // instantiator FIRST
        services.AddSingleton(pluginRegistry);   // plugin registry AFTER

        using var sp = services.BuildServiceProvider();
        var translator = sp.GetRequiredService<IAgentManifestTranslator>();
        var options = await translator.TranslateAsync(agentId);

        options.Agent.Should().BeSameAs(pluginAgent,
            because: "the translator resolves IPluginHandlerRegistry at activation; registering it after AddAgentManifestInstantiator still routes to the plugin.");
    }
}
