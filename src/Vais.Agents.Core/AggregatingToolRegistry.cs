// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Core;

/// <summary>
/// <see cref="IToolRegistry"/> that combines directly-registered <see cref="ITool"/>s
/// with tools discovered from one or more <see cref="IToolSource"/>s.
/// </summary>
/// <remarks>
/// <para>
/// Built via the async <see cref="BuildAsync"/> factory — discovery happens once at
/// build time and the result is cached so the <see cref="Tools"/> property stays
/// sync and honest. Consumers who need dynamic refresh ship their own
/// <see cref="IToolRegistry"/> implementation; this registry is intentionally
/// immutable after construction.
/// </para>
/// <para>
/// Duplicate names — whether between static tools and a source, or across two
/// sources — are resolved by first-wins order. Static tools come before source
/// tools; sources run in the order supplied. Consumers who care should ensure
/// unique names up front (e.g., namespace tool names with the source id).
/// </para>
/// </remarks>
public sealed class AggregatingToolRegistry : IToolRegistry
{
    private readonly IReadOnlyList<ITool> _tools;
    private readonly Dictionary<string, ITool> _byName;

    private AggregatingToolRegistry(IReadOnlyList<ITool> tools)
    {
        _tools = tools;
        _byName = new Dictionary<string, ITool>(tools.Count, StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            // First-wins on duplicate names. Later-discovered tools are silently
            // shadowed; the full list on Tools still carries them for visibility.
            _byName.TryAdd(tool.Name, tool);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ITool> Tools => _tools;

    /// <inheritdoc />
    public ITool? GetByName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _byName.TryGetValue(name, out var tool) ? tool : null;
    }

    /// <summary>
    /// Build a registry from static tools + dynamic sources. Discovery runs once
    /// in source order; the composed list is cached on the returned instance.
    /// </summary>
    /// <param name="staticTools">Tools registered directly. May be null or empty.</param>
    /// <param name="sources">Dynamic sources to enumerate. May be null or empty.</param>
    /// <param name="cancellationToken">Cancels ongoing discovery.</param>
    public static async Task<AggregatingToolRegistry> BuildAsync(
        IReadOnlyList<ITool>? staticTools,
        IReadOnlyList<IToolSource>? sources,
        CancellationToken cancellationToken = default)
    {
        var tools = new List<ITool>();
        if (staticTools is { Count: > 0 })
        {
            tools.AddRange(staticTools);
        }

        if (sources is { Count: > 0 })
        {
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await foreach (var tool in source.DiscoverAsync(cancellationToken).ConfigureAwait(false))
                {
                    tools.Add(tool);
                }
            }
        }

        return new AggregatingToolRegistry(tools);
    }
}
