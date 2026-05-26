// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Text;

namespace Vais.Agents.Control.Manifests;

/// <summary>
/// One tool surfaced into the south <c>tools/list</c> response. Transport-neutral —
/// concrete MCP <c>Tool</c> records map onto this shape before going through the cartridge,
/// and shaped descriptors map back onto the wire type at the dispatch edge.
/// </summary>
/// <param name="Name">Agent-visible tool name (the projected name on a virtual server).</param>
/// <param name="Description">Upstream tool description, or <c>null</c> if not provided.</param>
public sealed record ToolDescriptor(string Name, string? Description);

/// <summary>One shaped tool: the cartridge's view of a tool after the domain ontology overlay.</summary>
/// <param name="Name">Agent-visible tool name (unchanged from input).</param>
/// <param name="Description">Effective description — artifact override wins over upstream.</param>
/// <param name="Tags">Merged tags (artifact entry's tags; empty when the tool is unannotated).</param>
/// <param name="CrossRefs">Typed cross-references injected from the artifact.</param>
/// <param name="Hidden">True iff the tool matches the configured hide-tag set — operator-decided whether the agent ever sees it.</param>
public sealed record ShapedToolDescriptor(
    string Name,
    string? Description,
    IReadOnlyList<string> Tags,
    IReadOnlyList<OntologyConceptCrossRef> CrossRefs,
    bool Hidden);

/// <summary>Configuration for <see cref="DomainOntologyToolListShaper"/>.</summary>
public sealed record DomainOntologyToolListShaperOptions
{
    /// <summary>
    /// Tags whose presence on a tool flags it as <see cref="ShapedToolDescriptor.Hidden"/>.
    /// Default is empty — the cartridge annotates without hiding; deployers explicitly opt
    /// in to hiding by configuring this set (e.g. <c>risk:Destructive</c>).
    /// </summary>
    public IReadOnlySet<string> HideTags { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>
/// List-time south cartridge — shapes a virtual server's projected tool list using the
/// bound <see cref="IDomainOntologyCatalog"/>. Applies description rewrite, tag injection,
/// typed cross-ref injection, and (operator-configured) hide-tag flagging. Unknown tools
/// (not in the catalog scope) pass through unshaped — see Plan C1-8.
/// </summary>
/// <remarks>
/// Stateless and reentrant. Use <see cref="CachedDomainOntologyToolListShaper"/> when the
/// caller wants per-tool-list-hash caching to keep the shaping off the per-call hot path
/// (success criterion 6).
/// </remarks>
public sealed class DomainOntologyToolListShaper(DomainOntologyToolListShaperOptions? options = null)
{
    private readonly DomainOntologyToolListShaperOptions _options = options ?? new();

    /// <summary>Configured shaper options. Exposed for inspection / cache-key derivation.</summary>
    public DomainOntologyToolListShaperOptions Options => _options;

    /// <summary>Shape every input tool according to the catalog binding.</summary>
    public IReadOnlyList<ShapedToolDescriptor> Shape(
        IReadOnlyList<ToolDescriptor> tools,
        IDomainOntologyCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(catalog);

        var shaped = new ShapedToolDescriptor[tools.Count];
        for (var i = 0; i < tools.Count; i++)
        {
            var t = tools[i];
            if (!catalog.TryGetConcept(t.Name, out var concept))
            {
                shaped[i] = new ShapedToolDescriptor(t.Name, t.Description, [], [], Hidden: false);
                continue;
            }

            var effectiveDescription = concept.Description ?? t.Description;
            var tags = concept.Tags;
            var hidden = HasHideTag(tags);
            shaped[i] = new ShapedToolDescriptor(t.Name, effectiveDescription, tags, concept.CrossRefs, hidden);
        }
        return shaped;
    }

    private bool HasHideTag(IReadOnlyList<string> tags)
    {
        if (_options.HideTags.Count == 0 || tags.Count == 0) return false;
        for (var i = 0; i < tags.Count; i++)
            if (_options.HideTags.Contains(tags[i])) return true;
        return false;
    }
}

/// <summary>
/// Caches <see cref="DomainOntologyToolListShaper"/> output keyed by the input tool list
/// (names + descriptions, in order) and the catalog's <see cref="IOntologyBinding.OntologyVersion"/>.
/// Cache entries auto-invalidate when either input shape or ontology version changes; call
/// <see cref="Invalidate"/> for an explicit reset (e.g. on a hot-reload of the artifact).
/// </summary>
/// <remarks>
/// Plan C1-9 success criterion 6: list-time shaping must be off the per-call hot path. Wrap
/// the base shaper with this cache and the south cartridge re-shapes only when the upstream
/// tool list or the bound ontology actually changes.
/// </remarks>
public sealed class CachedDomainOntologyToolListShaper(DomainOntologyToolListShaper? inner = null)
{
    // Separators chosen from the ASCII control range so they never collide with characters in
    // tool names or descriptions: RS (0x1E) between records, US (0x1F) between fields.
    private const char RecordSep = '';
    private const char FieldSep = '';

    private readonly DomainOntologyToolListShaper _inner = inner ?? new DomainOntologyToolListShaper();
    private readonly ConcurrentDictionary<string, IReadOnlyList<ShapedToolDescriptor>> _cache = new(StringComparer.Ordinal);

    /// <summary>Count of distinct shaped tool-lists currently held in cache.</summary>
    public int Count => _cache.Count;

    /// <summary>Shape with caching by (tool-list, ontology version).</summary>
    public IReadOnlyList<ShapedToolDescriptor> Shape(
        IReadOnlyList<ToolDescriptor> tools,
        IDomainOntologyCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(catalog);
        var key = ComputeKey(tools, catalog);
        return _cache.GetOrAdd(key, _ => _inner.Shape(tools, catalog));
    }

    /// <summary>Drop every cached shape. Use after a domain-ontology artifact hot-reload.</summary>
    public void Invalidate() => _cache.Clear();

    private static string ComputeKey(IReadOnlyList<ToolDescriptor> tools, IDomainOntologyCatalog catalog)
    {
        var sb = new StringBuilder(64 + tools.Count * 32);
        sb.Append(catalog.OntologyVersion).Append(RecordSep);
        foreach (var t in tools)
        {
            sb.Append(t.Name).Append(FieldSep).Append(t.Description ?? "").Append(RecordSep);
        }
        return sb.ToString();
    }
}
