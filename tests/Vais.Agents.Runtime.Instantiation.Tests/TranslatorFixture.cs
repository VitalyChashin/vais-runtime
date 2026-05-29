// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Vais.Agents.Control;
using Vais.Agents.Runtime.Plugins;

namespace Vais.Agents.Runtime.Instantiation.Tests;

/// <summary>
/// Test fixture that assembles a working <see cref="IAgentManifestTranslator"/>
/// from mocks + a DI-backed <see cref="IStaticToolRegistry"/> /
/// <see cref="IPromptTemplateRegistry"/> / <see cref="IPromptFileLoader"/> as
/// needed. Each `WithX` returns the fixture for chained setup.
/// </summary>
internal sealed class TranslatorFixture
{
    private readonly FakeAgentRegistry _registry = new();
    private readonly List<IModelProviderFactory> _providers = new();
    private readonly Dictionary<string, ITool> _staticTools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _promptTemplates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _promptFiles = new(StringComparer.Ordinal);
    private readonly List<IGuardrailFactory> _guardrailFactories = new();
    private readonly List<IAgentHandlerFactory> _pluginFactories = new();
    private readonly List<INamedToolSourceProvider> _toolSourceProviders = new();
    private IManifestApplyDiagnosticsSink? _diagnosticsSink;
    private ILlmGatewayConfigRegistry? _llmGatewayConfigRegistry;
    private IMcpGatewayConfigRegistry? _mcpGatewayConfigRegistry;
    private IMcpServerRegistry? _mcpServerRegistry;
    private ILlmGatewayMiddlewareFactory? _llmGatewayFactory;
    private IToolGatewayMiddlewareFactory? _toolGatewayFactory;
    private Vais.Agents.Control.Manifests.IDomainOntologyArtifactRegistry? _domainOntologyRegistry;
    private Vais.Agents.Control.Manifests.IAgentCapabilityMapBuilder? _capabilityMapBuilder;
    private Vais.Agents.Control.Manifests.IDelegationPolicy? _delegationPolicy;
    private readonly List<LlmGatewayMiddleware> _diGlobalLlmMiddleware = new();
    private readonly List<ToolGatewayMiddleware> _diGlobalToolMiddleware = new();
    private ICodeModeToolFactory? _codeModeToolFactory;
    private IAgentManifestTranslator? _translator;

    public IAgentManifestTranslator Translator => _translator ??= Build();

    public TranslatorFixture WithManifest(AgentManifest manifest)
    {
        _registry.Add(manifest);
        _translator = null;
        return this;
    }

    public TranslatorFixture WithProvider(string providerName)
    {
        var provider = Substitute.For<ICompletionProvider>();
        provider.ProviderName.Returns(providerName);
        // SupportsResponseFormat defaults to false via DIM.

        var factory = Substitute.For<IModelProviderFactory>();
        factory.Provider.Returns(providerName);
        factory.CreateAsync(Arg.Any<ModelSpec>(), Arg.Any<ISecretResolver>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ICompletionProvider>(provider));

        _providers.Add(factory);
        _translator = null;
        return this;
    }

    public TranslatorFixture WithSupportingResponseFormatProvider(string providerName)
    {
        var provider = Substitute.For<ICompletionProvider>();
        provider.ProviderName.Returns(providerName);
        provider.SupportsResponseFormat.Returns(true);

        var factory = Substitute.For<IModelProviderFactory>();
        factory.Provider.Returns(providerName);
        factory.CreateAsync(Arg.Any<ModelSpec>(), Arg.Any<ISecretResolver>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ICompletionProvider>(provider));

        _providers.Add(factory);
        _translator = null;
        return this;
    }

    public TranslatorFixture WithStaticTool(string name, ITool tool)
    {
        _staticTools[name] = tool;
        _translator = null;
        return this;
    }

    public TranslatorFixture WithPromptTemplate(string name, string template)
    {
        _promptTemplates[name] = template;
        _translator = null;
        return this;
    }

    public TranslatorFixture WithPromptFile(string fileRef, string content)
    {
        _promptFiles[fileRef] = content;
        _translator = null;
        return this;
    }

    public TranslatorFixture WithPluginHandler(string handlerTypeName, Func<AgentManifest, IServiceProvider, IAiAgent> factory)
    {
        var wrapped = Substitute.For<IAgentHandlerFactory>();
        wrapped.HandlerTypeName.Returns(handlerTypeName);
        wrapped.CreateAsync(Arg.Any<AgentManifest>(), Arg.Any<IServiceProvider>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => new ValueTask<IAiAgent>(factory(callInfo.Arg<AgentManifest>(), callInfo.Arg<IServiceProvider>())));
        _pluginFactories.Add(wrapped);
        _translator = null;
        return this;
    }

    public TranslatorFixture WithPluginHandler(IAgentHandlerFactory factory)
    {
        _pluginFactories.Add(factory);
        _translator = null;
        return this;
    }

    public TranslatorFixture WithToolSourceProvider(INamedToolSourceProvider provider)
    {
        _toolSourceProviders.Add(provider);
        _translator = null;
        return this;
    }

    public TranslatorFixture WithDiagnosticsSink(IManifestApplyDiagnosticsSink sink)
    {
        _diagnosticsSink = sink;
        _translator = null;
        return this;
    }

    public TranslatorFixture WithLlmGatewayConfigRegistry(ILlmGatewayConfigRegistry registry)
    {
        _llmGatewayConfigRegistry = registry;
        _translator = null;
        return this;
    }

    public TranslatorFixture WithMcpGatewayConfigRegistry(IMcpGatewayConfigRegistry registry)
    {
        _mcpGatewayConfigRegistry = registry;
        _translator = null;
        return this;
    }

    public TranslatorFixture WithMcpServerRegistry(IMcpServerRegistry registry)
    {
        _mcpServerRegistry = registry;
        _translator = null;
        return this;
    }

    public TranslatorFixture WithDomainOntologyRegistry(Vais.Agents.Control.Manifests.IDomainOntologyArtifactRegistry registry)
    {
        _domainOntologyRegistry = registry;
        _translator = null;
        return this;
    }

    public TranslatorFixture WithCapabilityMapBuilder(Vais.Agents.Control.Manifests.IAgentCapabilityMapBuilder builder)
    {
        _capabilityMapBuilder = builder;
        _translator = null;
        return this;
    }

    public TranslatorFixture WithDelegationPolicy(Vais.Agents.Control.Manifests.IDelegationPolicy policy)
    {
        _delegationPolicy = policy;
        _translator = null;
        return this;
    }

    public TranslatorFixture WithLlmGatewayFactory(ILlmGatewayMiddlewareFactory factory)
    {
        _llmGatewayFactory = factory;
        _translator = null;
        return this;
    }

    public TranslatorFixture WithToolGatewayFactory(IToolGatewayMiddlewareFactory factory)
    {
        _toolGatewayFactory = factory;
        _translator = null;
        return this;
    }

    public TranslatorFixture WithDiGlobalLlmMiddleware(LlmGatewayMiddleware middleware)
    {
        _diGlobalLlmMiddleware.Add(middleware);
        _translator = null;
        return this;
    }

    public TranslatorFixture WithDiGlobalToolMiddleware(ToolGatewayMiddleware middleware)
    {
        _diGlobalToolMiddleware.Add(middleware);
        _translator = null;
        return this;
    }

    public TranslatorFixture WithCodeModeToolFactory(ICodeModeToolFactory factory)
    {
        _codeModeToolFactory = factory;
        _translator = null;
        return this;
    }

    public TranslatorFixture WithGuardrailFactory(string name, GuardrailLayer layer, object instance)
    {
        var factory = Substitute.For<IGuardrailFactory>();
        factory.Name.Returns(name);
        factory.Layer.Returns(layer);
        factory.Create(Arg.Any<System.Text.Json.JsonElement?>(), Arg.Any<IServiceProvider>()).Returns(instance);
        _guardrailFactories.Add(factory);
        _translator = null;
        return this;
    }

    private IAgentManifestTranslator Build()
    {
        var services = new ServiceCollection();

        var secretsResolver = Substitute.For<ISecretResolver>();
        secretsResolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<string>("secret-value"));

        services.AddSingleton(secretsResolver);
        services.AddSingleton<IAgentRegistry>(_registry);

        foreach (var provider in _providers)
        {
            services.AddSingleton(provider);
        }

        foreach (var factory in _guardrailFactories)
        {
            services.AddSingleton(factory);
        }

        if (_staticTools.Count > 0)
        {
            var toolMap = _staticTools;
            services.AddStaticToolRegistry(builder =>
            {
                foreach (var (name, tool) in toolMap)
                {
                    builder.Add(name, _ => tool);
                }
            });
        }

        if (_promptTemplates.Count > 0)
        {
            var templateMap = _promptTemplates;
            services.AddPromptTemplateRegistry(builder =>
            {
                foreach (var (name, template) in templateMap)
                {
                    builder.Add(name, template);
                }
            });
        }

        if (_promptFiles.Count > 0)
        {
            services.AddSingleton<IPromptFileLoader>(new FakePromptFileLoader(_promptFiles));
        }

        if (_pluginFactories.Count > 0)
        {
            var registry = new FakePluginHandlerRegistry(_pluginFactories);
            services.AddSingleton<IPluginHandlerRegistry>(registry);
        }

        if (_diagnosticsSink is not null)
        {
            services.AddSingleton<IManifestApplyDiagnosticsSink>(_diagnosticsSink);
        }

        foreach (var provider in _toolSourceProviders)
        {
            services.AddSingleton<INamedToolSourceProvider>(provider);
        }

        if (_llmGatewayConfigRegistry is not null)
            services.AddSingleton(_llmGatewayConfigRegistry);
        if (_mcpGatewayConfigRegistry is not null)
            services.AddSingleton(_mcpGatewayConfigRegistry);
        if (_mcpServerRegistry is not null)
            services.AddSingleton(_mcpServerRegistry);
        if (_llmGatewayFactory is not null)
            services.AddSingleton(_llmGatewayFactory);
        if (_toolGatewayFactory is not null)
            services.AddSingleton(_toolGatewayFactory);
        if (_domainOntologyRegistry is not null)
            services.AddSingleton(_domainOntologyRegistry);
        if (_capabilityMapBuilder is not null)
            services.AddSingleton(_capabilityMapBuilder);
        if (_delegationPolicy is not null)
            services.AddSingleton(_delegationPolicy);
        foreach (var mw in _diGlobalLlmMiddleware)
            services.AddSingleton<LlmGatewayMiddleware>(mw);
        foreach (var mw in _diGlobalToolMiddleware)
            services.AddSingleton<ToolGatewayMiddleware>(mw);

        if (_codeModeToolFactory is not null)
            services.AddSingleton(_codeModeToolFactory);

        services.AddAgentManifestInstantiator();

        return services.BuildServiceProvider().GetRequiredService<IAgentManifestTranslator>();
    }

    private sealed class FakePluginHandlerRegistry : IPluginHandlerRegistry
    {
        private readonly Dictionary<string, IAgentHandlerFactory> _byName;

        public FakePluginHandlerRegistry(IEnumerable<IAgentHandlerFactory> factories)
        {
            _byName = factories.ToDictionary(f => f.HandlerTypeName, StringComparer.Ordinal);
        }

        public bool TryGet(string handlerTypeName, out IAgentHandlerFactory? factory)
        {
            if (_byName.TryGetValue(handlerTypeName, out var f))
            {
                factory = f;
                return true;
            }
            factory = null;
            return false;
        }

        public IReadOnlyCollection<string> HandlerTypeNames => _byName.Keys;

        public IReadOnlyCollection<PluginDescriptor> Plugins => Array.Empty<PluginDescriptor>();

        public void Register(IAgentHandlerFactory factory, string ownerPluginName)
            => _byName[factory.HandlerTypeName] = factory;
    }

    private sealed class FakeAgentRegistry : IAgentRegistry
    {
        private readonly List<AgentManifest> _manifests = new();

        public void Add(AgentManifest manifest) => _manifests.Add(manifest);

        public async IAsyncEnumerable<AgentManifest> ListAsync(string? labelPrefix = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            foreach (var manifest in _manifests)
            {
                yield return manifest;
            }
        }

        public ValueTask<AgentManifest?> GetAsync(string id, string? version = null, CancellationToken cancellationToken = default)
        {
            var match = _manifests.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal));
            return new ValueTask<AgentManifest?>(match);
        }
    }

    private sealed class FakePromptFileLoader : IPromptFileLoader
    {
        private readonly IReadOnlyDictionary<string, string> _files;

        public FakePromptFileLoader(IReadOnlyDictionary<string, string> files)
        {
            _files = files;
        }

        public ValueTask<string> LoadAsync(string fileRef, CancellationToken cancellationToken = default)
        {
            if (!_files.TryGetValue(fileRef, out var content))
            {
                throw new ManifestInstantiationException(
                    ManifestInstantiationUrns.PromptFileUnreadable,
                    $"Fake loader has no content for '{fileRef}'.");
            }
            return new ValueTask<string>(content);
        }
    }
}
