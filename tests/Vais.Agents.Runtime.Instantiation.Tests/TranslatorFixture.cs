// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Vais.Agents.Control;

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

        services.AddAgentManifestInstantiator();

        return services.BuildServiceProvider().GetRequiredService<IAgentManifestTranslator>();
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
