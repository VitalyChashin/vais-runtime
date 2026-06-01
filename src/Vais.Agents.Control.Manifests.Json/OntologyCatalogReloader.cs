// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Manifests;

/// <summary>
/// Plan D D-14 — rebuilds the in-process <see cref="IOntologyCatalog"/> after a
/// successful overlay write (by <see cref="IOntologyOverlayWriter"/>). When the
/// runtime is wired with this reloader, an approved <see cref="RecipeProposal"/>
/// landing in the overlay file is visible to <c>vais.describe</c> and downstream
/// consumers on the next read — no runtime restart required.
/// </summary>
public interface IOntologyCatalogReloader
{
    /// <summary>
    /// Re-read the overlay from the configured path and atomically swap the catalog
    /// the runtime serves through <see cref="IOntologyCatalog"/>. Returns the new catalog.
    /// </summary>
    Task<IOntologyCatalog> ReloadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reloads <see cref="IFailureOntologyCatalog"/> after a failure overlay write.
/// Symmetric with <see cref="IOntologyCatalogReloader"/> for the behaviour ontology but
/// uses a dedicated interface to avoid DI ambiguity and carry the correct return type.
/// </summary>
public interface IFailureOntologyCatalogReloader
{
    /// <summary>
    /// Re-read the overlay directory and atomically swap the catalog the runtime serves
    /// through <see cref="IFailureOntologyCatalog"/>. Returns the new catalog.
    /// </summary>
    Task<IFailureOntologyCatalog> ReloadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IOntologyCatalogReloader"/> + a mutable
/// <see cref="IOntologyCatalog"/> facade rolled into one. Composition root
/// registers a single instance as both <see cref="IOntologyCatalog"/> and
/// <see cref="IOntologyCatalogReloader"/>; consumers see fresh entries after
/// every successful <see cref="ReloadAsync"/>.
/// </summary>
/// <remarks>
/// The facade forwards every <see cref="IOntologyCatalog"/> call to a
/// <see cref="Volatile.Read{T}"/>-guarded inner reference. Replacement is a
/// single-pointer swap — readers in flight finish against the old catalog and
/// subsequent calls see the new one. No locks on the read path.
/// </remarks>
public sealed class HotReloadableOntologyCatalog : IOntologyCatalog, IOntologyCatalogReloader
{
    private IOntologyCatalog _inner;
    private readonly string? _overlayPath;
    private readonly object _reloadLock = new();

    /// <summary>Build the facade. <paramref name="initial"/> is the catalog at startup.</summary>
    public HotReloadableOntologyCatalog(IOntologyCatalog initial, string? overlayPath)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _inner = initial;
        _overlayPath = overlayPath;
    }

    private IOntologyCatalog Current => Volatile.Read(ref _inner);

    /// <inheritdoc />
    public Task<IOntologyCatalog> ReloadAsync(CancellationToken cancellationToken = default)
    {
        // Single-flight: only one reload in flight; subsequent callers see the post-swap catalog.
        IOntologyCatalog rebuilt;
        lock (_reloadLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var overlay = OntologyOverlayLoader.LoadFromFile(_overlayPath);
            rebuilt = OntologyCatalog.BuildFromEmbeddedBase(overlay);
            Volatile.Write(ref _inner, rebuilt);
        }
        return Task.FromResult(rebuilt);
    }

    // ── IOntologyCatalog forwarding ───────────────────────────────────────────

    /// <inheritdoc />
    public KindOntologyEntry Get(string kind) => Current.Get(kind);

    /// <inheritdoc />
    public bool TryGet(string kind, out KindOntologyEntry entry) => Current.TryGet(kind, out entry);

    /// <inheritdoc />
    public IReadOnlyList<string> Kinds => Current.Kinds;

    /// <inheritdoc />
    public IReadOnlyList<RecipeEntry> Recipes => Current.Recipes;

    /// <inheritdoc />
    public string OntologyVersion => ((IOntologyBinding)Current).OntologyVersion;

    // ── IOntologyBinding forwarding ───────────────────────────────────────────

    IReadOnlyList<string> IOntologyBinding.ConceptNames => ((IOntologyBinding)Current).ConceptNames;

    bool IOntologyBinding.TryGetConcept(string conceptName, out OntologyConceptEntry entry)
        => ((IOntologyBinding)Current).TryGetConcept(conceptName, out entry);
}
