# Attach a domain ontology to a virtual MCP server

The south cartridge lets you layer a **domain ontology** over an MCP server's tool surface — annotating tools with capability/risk tags, rewriting descriptions for the model, asserting which arguments are semantically required, and enriching responses with ontology-derived metadata. The cartridge machinery is OSS; the ontology **content** (your tool annotations) stays deployment-local and is never checked into `agentic/`.

This guide covers:

1. Author a `*.domain-ontology.json` artifact.
2. Bind it to a virtual MCP server through `McpServerManifest.OntologyRef`.
3. Use the resulting `IDomainOntologyCatalog` with the shaper (tools/list), retriever (top-K ranking), or call-time middleware (arg validation + response enrichment).

The substrate that makes this work (and the symmetric north cartridge for the design-tools MCP server) is described in [docs/concepts/ontology-substrate.md](../concepts/ontology-substrate.md).

## When to use it

The cartridge is the right tool when you have:

- A virtual MCP server that aggregates many upstream tools (Kubernetes, internal data tools, etc.) and an agent that struggles to pick the right one or calls them with missing arguments.
- An operational policy that wants destructive or sensitive tools surfaced with explicit tags even when the upstream MCP server returns plain descriptions.
- A retrieval-style narrowing step where the agent's catalog is wide enough that you want to score and shortlist tools per turn instead of dumping the whole list into the prompt.

If your only need is a static deny list or a uniform output truncation, the existing [Tool Gateway middlewares](gate-tool-calls-with-the-tool-gateway.md) cover that more directly.

## 1. Author the artifact

A domain-ontology artifact is a JSON file keyed by the agent-visible tool name. Save it under any deployment-local path — for the directory loader it must end in `.domain-ontology.json`:

```json
{
  "ontologyVersion": "k8s-tools-v1",
  "tools": {
    "kubectl_get": {
      "description": "Read-only inspection of a Kubernetes resource by kind + name.",
      "tags": ["risk:read", "category:kubernetes"],
      "crossRefs": [
        { "fieldPath": "resource.kind", "targetConceptName": "K8sKind",  "cardinality": "one" },
        { "fieldPath": "resource.name", "targetConceptName": "K8sName",  "cardinality": "one" }
      ]
    },
    "kubectl_delete": {
      "description": "Delete a Kubernetes resource. Destructive — requires confirmation.",
      "tags": ["risk:Destructive", "category:kubernetes"],
      "crossRefs": [
        { "fieldPath": "resource.kind", "targetConceptName": "K8sKind", "cardinality": "one" },
        { "fieldPath": "resource.name", "targetConceptName": "K8sName", "cardinality": "one" }
      ]
    }
  }
}
```

Fields:

| Field | Meaning |
|---|---|
| `ontologyVersion` | Stamp carried into telemetry so a chain run can be correlated with the exact ontology snapshot. Bump it when you change content. |
| `tools` | Per-tool annotations keyed by the **agent-visible** tool name (the projected name on the virtual server, not necessarily the upstream tool name). |
| `tools[name].description` | Overrides the upstream description for the agent. Omit to keep the upstream's. |
| `tools[name].tags` | Free-form capability / risk markers. The cartridge reads them for filtering and enrichment; deployers can attach any vocabulary. |
| `tools[name].crossRefs` | Typed cross-references from arguments to other concepts. Used by the request-phase validator to short-circuit missing-argument calls. |

Tools mentioned in the upstream `tools/list` but **not** in the artifact pass through unshaped — the cartridge only annotates what you explicitly cover.

## 2. Load and register the artifact

Use `DomainOntologyArtifactLoader` to read the file or directory at startup, and the `IDomainOntologyArtifactRegistry` to make it resolvable by name:

```csharp
using Vais.Agents.Control.Manifests;

var services = new ServiceCollection();
services.AddSingleton<IDomainOntologyArtifactRegistry>(_ =>
{
    var registry = new InMemoryDomainOntologyArtifactRegistry();
    // Single artifact by absolute path:
    registry.Register("k8s-tools-v1",
        DomainOntologyArtifactLoader.LoadFromFile("/etc/vais/ontologies/k8s-tools.domain-ontology.json"));
    // Or scan a directory of *.domain-ontology.json files in one call:
    registry.RegisterAll(
        DomainOntologyArtifactLoader.LoadAllFromDirectory("/etc/vais/ontologies"));
    return registry;
});
```

The directory loader keys each artifact by its filename stem: `k8s-tools.domain-ontology.json` registers as `k8s-tools`. Malformed JSON files are skipped silently so a typo in one artifact never takes the whole cartridge offline.

A missing or unknown ref returns `null` from `Get` — the cartridge degrades gracefully to passthrough.

## 3. Bind the artifact to a virtual MCP server

Set `OntologyRef` on the `McpServerManifest` to the registered artifact name:

```yaml
apiVersion: vais.agents/v1
kind: McpServer
metadata:
  id: kubernetes-virtual
  version: 1.0
spec:
  virtual: true
  ontologyRef: k8s-tools-v1
  sources:
    - { ref: kubectl-mcp }
  toolProjection:
    - { name: kubectl_get,    from: kubectl-mcp }
    - { name: kubectl_delete, from: kubectl-mcp }
```

The field auto-serializes through `EnvelopeCodec` — `vais get McpServer/kubernetes-virtual` re-emits the ref intact for a re-applicable round-trip.

## 4. Build a catalog and use it

Construct a `DomainOntologyCatalog` per virtual server. The catalog scope is the projected tool name set; the artifact layers tags, descriptions, and cross-refs on top:

```csharp
using Vais.Agents.Control.Manifests;

var artifact = registry.Get(serverManifest.OntologyRef!) ?? DomainOntologyArtifact.Empty;
var projection = serverManifest.ToolProjection?.Select(p => p.Name).ToList() ?? [];
IDomainOntologyCatalog catalog = new DomainOntologyCatalog(artifact, projection);
```

The catalog satisfies the cross-cutting `IOntologyBinding` seam — any interceptor written against the seam works against it. The three cartridge components below are deployment-supplied building blocks; mix in only what you need.

### List-time: shape the agent-visible tool list

```csharp
var shaper = new CachedDomainOntologyToolListShaper(
    new DomainOntologyToolListShaper(new DomainOntologyToolListShaperOptions
    {
        HideTags = new HashSet<string>(StringComparer.Ordinal) { "risk:Destructive" },
    }));

var upstreamTools = new[]
{
    new ToolDescriptor("kubectl_get",    "Get a Kubernetes resource."),
    new ToolDescriptor("kubectl_delete", "Delete a Kubernetes resource."),
};
var shaped = shaper.Shape(upstreamTools, catalog);
// → shaped[0] = (kubectl_get, "Read-only inspection...", ["risk:read","category:kubernetes"], [...], Hidden=false)
// → shaped[1] = (kubectl_delete, "Delete a Kubernetes resource. Destructive...", ["risk:Destructive",...], Hidden=true)
```

The cache keys on `(input tool list, ontology version)` — same inputs return the identical instance, and a hot-reload that bumps `ontologyVersion` auto-invalidates entries. Call `shaper.Invalidate()` for an explicit reset after registering a new artifact at the same name.

**Default is annotate-only** — `Hidden` flips to true only when you supply `HideTags`. Keep this transparent by default; opt into hiding when an operator policy says so.

> **Note (Plan C1):** the list-time shaper is presently a standalone tested component. Wiring it into the per-virtual-server tool-list assembly path that the runtime takes at agent activation is a follow-on commit. Until that lands, deployers can drive the shaper directly from their own composition root or a custom `AgentInputMiddleware`.

### Retrieval: rank top-K tools for the agent's intent

When a virtual server exposes more tools than you want in the prompt, score and shortlist:

```csharp
IToolRetriever lexical = LexicalToolRetriever.ForCatalog(catalog);
// Optional semantic rerank — requires an IEmbeddingGenerator<string, Embedding<float>>.
IToolRetriever retriever = embedder is null
    ? lexical
    : new SemanticToolRetriever(lexical, embedder);

var topK = await retriever.RetrieveAsync(
    query: "I need to inspect a pod in the staging namespace",
    candidates: upstreamTools,
    topK: 5);
// → topK[0..n] sorted by score; lexical-only by default, rerank by cosine similarity when embedder is registered.
```

The lexical retriever is always-on and dependency-free (weights name ×3, tags ×2, description ×1). The semantic retriever is a decorator that activates only when a `Microsoft.Extensions.AI` embedder is present in DI; without one, you keep the lexical path. The `IToolClassifier` interface is a hook for an additional re-rank step (e.g. an LLM-backed scorer) — no default impl ships; supply your own.

### Call-time: validate arguments + enrich responses

Wire the two `ToolGatewayMiddleware` derivatives into your gateway config for the virtual server:

```csharp
ToolGatewayMiddleware[] middleware =
[
    new DomainOntologyArgValidationMiddleware(catalog),   // request-phase, Kind = Validation
    new DomainOntologyResponseEnrichmentMiddleware(catalog), // response-phase, Kind = Mutation
];
```

The validator inspects every call against the bound concept's `crossRefs`: if any `fieldPath` resolves to a missing or empty value in the arguments, the call short-circuits with

```json
{
  "ok": false,
  "reason": "missing required argument(s) referenced by domain ontology: resource.kind, resource.name",
  "suggestions": [
    "provide a value for argument 'resource.kind'",
    "provide a value for argument 'resource.name'"
  ]
}
```

The upstream tool is **never** invoked on a short-circuit, so a bad call costs nothing downstream.

The enrichment middleware appends an `_ontology` block to the response when the result parses as a JSON object and the bound concept carries tags. Plain-text outputs and error outcomes pass through unchanged.

## Behavior matrix

| Situation | Cartridge effect |
|---|---|
| Tool in upstream, not in projection scope | Out of catalog scope ⇒ TryGetConcept = false ⇒ passthrough on every stage. |
| Tool in projection, not in artifact | Concept exists with empty annotations. Shaper preserves the upstream description; validator and enricher pass through. |
| Tool in artifact, deployer hot-reloads with new `ontologyVersion` | Cached shape entries auto-invalidate on next call; explicit `Invalidate()` available. |
| `OntologyRef` points to an unregistered name | Registry returns `null` ⇒ cartridge applies passthrough; no error. |

## Related

- [docs/concepts/ontology-substrate.md](../concepts/ontology-substrate.md) — SEP-1763 substrate that powers both north (resource model) and south (this guide) cartridges.
- [docs/guides/gate-tool-calls-with-the-tool-gateway.md](gate-tool-calls-with-the-tool-gateway.md) — the underlying Tool Gateway pipeline the call-time middleware plugs into.
