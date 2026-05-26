// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Runtime.Instantiation;

/// <summary>
/// Aggregates one or more upstream <see cref="IToolSource"/>s into a single virtual source.
/// When a <see cref="McpServerToolProjection"/> list is provided, only the projected tools are
/// exposed and names are remapped accordingly. Without a projection, all tools from all upstream
/// sources are exposed (first-source-wins on name collision).
/// </summary>
/// <remarks>
/// Optional <c>shape</c> callback (Plan C1-9 south cartridge): when non-null the projected
/// tool list is collected, run through the shaper, then yielded. Shaped tools may carry
/// rewritten descriptions or be filtered out entirely.
/// </remarks>
internal sealed class VirtualMcpToolSource : IToolSource
{
    private readonly IReadOnlyList<(IToolSource Source, string ServerId)> _sources;
    private readonly IReadOnlyList<McpServerToolProjection>? _projection;
    private readonly Func<IReadOnlyList<ITool>, IReadOnlyList<ITool>>? _shape;

    internal VirtualMcpToolSource(
        IReadOnlyList<(IToolSource Source, string ServerId)> sources,
        IReadOnlyList<McpServerToolProjection>? projection,
        Func<IReadOnlyList<ITool>, IReadOnlyList<ITool>>? shape = null)
    {
        _sources = sources;
        _projection = projection;
        _shape = shape;
    }

    public async IAsyncEnumerable<ITool> DiscoverAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var raw = await CollectAsync(cancellationToken).ConfigureAwait(false);
        if (_shape is null)
        {
            foreach (var t in raw) yield return t;
            yield break;
        }
        foreach (var t in _shape(raw)) yield return t;
    }

    private async ValueTask<IReadOnlyList<ITool>> CollectAsync(CancellationToken cancellationToken)
    {
        if (_projection is null)
        {
            // Expose all tools from all sources; first-source-wins on name collision.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var all = new List<ITool>();
            foreach (var (source, _) in _sources)
            {
                await foreach (var tool in source.DiscoverAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (seen.Add(tool.Name)) all.Add(tool);
                }
            }
            return all;
        }

        // Projection mode: discover each referenced source once, then collect only the
        // projected tools, remapping names where SourceToolName differs.
        var toolsByServer = new Dictionary<string, Dictionary<string, ITool>>(StringComparer.Ordinal);
        foreach (var entry in _projection)
        {
            if (toolsByServer.ContainsKey(entry.From)) continue;
            var (source, _) = FindSource(entry.From);
            var tools = new Dictionary<string, ITool>(StringComparer.Ordinal);
            await foreach (var tool in source.DiscoverAsync(cancellationToken).ConfigureAwait(false))
                tools.TryAdd(tool.Name, tool);
            toolsByServer[entry.From] = tools;
        }

        var projected = new List<ITool>(_projection.Count);
        foreach (var entry in _projection)
        {
            var upstreamName = entry.SourceToolName ?? entry.Name;
            if (!toolsByServer[entry.From].TryGetValue(upstreamName, out var upstream))
                throw new InvalidOperationException(
                    $"Virtual server projection references tool '{upstreamName}' on source '{entry.From}' " +
                    $"which was not discovered. Available: [{string.Join(", ", toolsByServer[entry.From].Keys)}].");
            projected.Add(string.Equals(upstream.Name, entry.Name, StringComparison.Ordinal)
                ? upstream
                : new RenamedTool(upstream, entry.Name));
        }
        return projected;
    }

    private (IToolSource Source, string ServerId) FindSource(string serverId)
    {
        foreach (var entry in _sources)
        {
            if (string.Equals(entry.ServerId, serverId, StringComparison.Ordinal))
                return entry;
        }
        throw new InvalidOperationException(
            $"Virtual server projection references source '{serverId}' which is not in the upstream sources list. " +
            $"Available sources: [{string.Join(", ", _sources.Select(s => s.ServerId))}].");
    }

    private sealed class RenamedTool : ITool
    {
        private readonly ITool _inner;

        internal RenamedTool(ITool inner, string name)
        {
            _inner = inner;
            Name = name;
        }

        public string Name { get; }
        public string Description => _inner.Description;
        public JsonElement ParametersSchema => _inner.ParametersSchema;
        public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
            => _inner.InvokeAsync(arguments, cancellationToken);
    }

    /// <summary>
    /// Wraps a tool to override only its <see cref="ITool.Description"/>. Used by the
    /// Plan C1-9 shaper to apply ontology description rewrites without touching the
    /// upstream tool's identity, schema, or invocation path.
    /// </summary>
    internal sealed class DescribedTool : ITool
    {
        private readonly ITool _inner;

        internal DescribedTool(ITool inner, string description)
        {
            _inner = inner;
            Description = description;
        }

        public string Name => _inner.Name;
        public string Description { get; }
        public JsonElement ParametersSchema => _inner.ParametersSchema;
        public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
            => _inner.InvokeAsync(arguments, cancellationToken);
    }
}
