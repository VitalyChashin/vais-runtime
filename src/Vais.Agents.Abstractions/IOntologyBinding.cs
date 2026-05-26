// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Marker for an ontology bound at an interception site. Concrete catalogs
/// (north <c>IOntologyCatalog</c> over the resource model, south
/// <c>IDomainOntologyCatalog</c> over a virtual MCP server's tools) implement this seam so
/// that an <see cref="OntologyInterceptor"/> can be written against the substrate without
/// knowing which transport it is observing.
/// </summary>
/// <remarks>
/// C1-1 ships only the version surface — enough for the chain primitives and for binding-aware
/// observability. C1-3 fleshes the seam with concept lookup, tags, and cross-refs once both
/// catalogs are in place.
/// </remarks>
public interface IOntologyBinding
{
    /// <summary>
    /// Version of the bound ontology artifact (typically the catalog's content-hash or the
    /// overlay version). Carried into telemetry so a chain run can be correlated with the
    /// exact ontology snapshot that shaped it.
    /// </summary>
    string OntologyVersion { get; }
}
