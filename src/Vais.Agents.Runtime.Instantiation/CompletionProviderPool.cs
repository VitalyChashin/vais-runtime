// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using Vais.Agents.Control;

namespace Vais.Agents.Runtime.Instantiation;

internal sealed class CompletionProviderPool : ICompletionProviderPool
{
    private readonly ConcurrentDictionary<ModelSpec, Lazy<Task<ICompletionProvider>>> _cache = new();
    private readonly IReadOnlyDictionary<string, IModelProviderFactory> _factoriesByProvider;
    private readonly ISecretResolver _secrets;

    public CompletionProviderPool(IEnumerable<IModelProviderFactory> factories, ISecretResolver secrets)
    {
        ArgumentNullException.ThrowIfNull(factories);
        ArgumentNullException.ThrowIfNull(secrets);

        var map = new Dictionary<string, IModelProviderFactory>(StringComparer.OrdinalIgnoreCase);
        foreach (var factory in factories)
        {
            if (!map.TryAdd(factory.Provider, factory))
            {
                throw new InvalidOperationException(
                    $"More than one IModelProviderFactory registered for provider '{factory.Provider}'. Provider names must be unique.");
            }
        }

        _factoriesByProvider = map;
        _secrets = secrets;
    }

    public async ValueTask<ICompletionProvider> GetAsync(ModelSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var lazy = _cache.GetOrAdd(spec, key => new Lazy<Task<ICompletionProvider>>(
            () => CreateProviderAsync(key, cancellationToken).AsTask(),
            LazyThreadSafetyMode.ExecutionAndPublication));

        return await lazy.Value.ConfigureAwait(false);
    }

    private async ValueTask<ICompletionProvider> CreateProviderAsync(ModelSpec spec, CancellationToken cancellationToken)
    {
        if (!_factoriesByProvider.TryGetValue(spec.Provider, out var factory))
        {
            var registered = _factoriesByProvider.Count == 0 ? "(none)" : string.Join(", ", _factoriesByProvider.Keys);
            throw new ManifestInstantiationException(
                ManifestInstantiationUrns.ModelProviderUnsupported,
                $"No IModelProviderFactory registered for provider '{spec.Provider}'. Registered: {registered}.");
        }

        return await factory.CreateAsync(spec, _secrets, cancellationToken).ConfigureAwait(false);
    }
}
