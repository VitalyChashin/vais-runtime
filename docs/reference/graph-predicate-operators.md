# Reference: graph predicate operators

`GraphEdgePredicate` is the closed vocabulary used on every `GraphEdge.When` slot. Seven subtypes, one ten-value `GraphPredicateOperator` enum, two escape hatches.

Parallel to Kubernetes `matchExpressions` on a `PodSelector` — deliberately familiar for operators and YAML-authored graphs. Inline [PowerFx](https://learn.microsoft.com/en-us/power-platform/power-fx/overview) expressions (`=...`) are also supported for simple conditions that the `PropertyMatcher` vocabulary can't cover.

## Subtype summary

| Subtype | Purpose | YAML shape |
|---|---|---|
| `Always` | Unconditional match. Equivalent to `When: null`. | `when: always` |
| `PropertyMatcher` | Compare a state property against a literal value via a `GraphPredicateOperator`. | `when: { property, operator, value? }` |
| `AllOf` | True when every nested predicate matches. | `when: { allOf: [ … ] }` |
| `AnyOf` | True when at least one nested predicate matches. | `when: { anyOf: [ … ] }` |
| `Not` | Inversion. | `when: { not: { … } }` |
| `Expression` | Inline PowerFx boolean expression. State keys exposed as `Local.*`. | `when: "=<expr>"` |
| `HandlerRef` | Dispatch to a DI-resolved `IGraphEdgePredicate`. Escape hatch for predicates beyond the matcher vocabulary. | `when: { handlerRef: { typeName, assemblyName? } }` |

Edges from the same source node are evaluated in **manifest order**; first match wins. At least one `Always` (or unguarded) edge per source node is the convention for guaranteed reachability of `End`.

## Operators

Ten operators, set on `PropertyMatcher.Operator`:

| Operator | Semantics | Value type | Works on |
|---|---|---|---|
| `Eq` | Equality. | scalar | string, number, bool |
| `NotEq` | Inverse equality. | scalar | string, number, bool |
| `Gt` | Strictly greater than. | number | number only |
| `Gte` | Greater than or equal. | number | number only |
| `Lt` | Strictly less than. | number | number only |
| `Lte` | Less than or equal. | number | number only |
| `Contains` | Case-sensitive containment. | scalar | string substring match, or array-of-scalars membership |
| `NotContains` | Inverse containment. | scalar | same as `Contains` |
| `Exists` | Property present at path (any value, including `null`). | (ignored) | any |
| `NotExists` | Property absent. | (ignored) | any |

Numeric operators (`Gt` / `Gte` / `Lt` / `Lte`) treat the property and value as `double`. Integer fields compare exactly; floating-point comparisons use direct `double.CompareTo`, so callers own any tolerance semantics — the runtime doesn't inject an epsilon.

`Contains` / `NotContains` on strings is substring match; on arrays it's element-level match (exact, case-sensitive).

## Dotted property paths

`PropertyMatcher.Property` is a dotted path into the state bag. Supported forms:

| Path | Resolves to |
|---|---|
| `category` | Top-level state key. |
| `user.role` | Nested object property (JSON object traversal). |
| `lastMessage.text` / `lastMessage.role` | Well-known paths — read the most-recently-appended entry from the `messages` state key. Same convention as LangGraph. |
| `resume.payload.approved` | The caller-supplied resume payload, rehydrated under `state["resume.payload"]` at `IResumableAgentGraph<TState>.ResumeAsync` time. |

Missing intermediate segments resolve to "property absent" — `Exists` returns false; comparison operators return false; `NotExists` returns true. No exceptions are thrown for missing paths.

## JSON / YAML shape examples

### `Always`

```yaml
- from: classify
  to: done
  when: always
```

### `PropertyMatcher`

```yaml
- from: classify
  to: handle-billing
  when:
    property: category
    operator: Eq
    value: billing
```

Numeric:

```yaml
- from: grade
  to: done
  when: { property: quality, operator: Gte, value: 0.7 }
```

Existence:

```yaml
- from: load
  to: process
  when: { property: docs, operator: Exists }
```

Array contains:

```yaml
- from: route
  to: vip-queue
  when: { property: user.tags, operator: Contains, value: vip }
```

### `AllOf` + `AnyOf` + `Not`

Retry the retrieval loop only when quality is low **and** we haven't exhausted the retry budget:

```yaml
- from: grade
  to: retrieve
  when:
    allOf:
      - { property: quality,    operator: Lt, value: 0.7 }
      - { property: retryCount, operator: Lt, value: 3 }
  onTraverse:
    increment: { property: retryCount }
```

Match any of several categories:

```yaml
- from: classify
  to: specialist
  when:
    anyOf:
      - { property: category, operator: Eq, value: billing }
      - { property: category, operator: Eq, value: technical }
      - { property: category, operator: Eq, value: compliance }
```

Inversion — route to fallback when the standard classifier didn't produce a category:

```yaml
- from: classify
  to: fallback
  when:
    not:
      property: category
      operator: Exists
```

Combinators nest freely. `AllOf` / `AnyOf` take an empty array too — `AllOf: []` matches (vacuously true); `AnyOf: []` does not match (vacuously false).

### `Expression` — inline PowerFx

Use when a `PropertyMatcher` combination would be awkward — "compare two state properties", "check for blank vs. absent", "combine multiple conditions in one expression":

```yaml
- from: planner
  to: analyst
  when: "=Not(IsBlank(Local.research_plan))"

- from: planner
  to: end
  when: "=IsBlank(Local.research_plan)"
```

Expressions start with `=` (identical to PowerFx / Power Apps convention). Everything after `=` is the formula.

**State namespace** — every state key is available as `Local.<key>`. Hyphens in key names are normalised to underscores (`research-plan` → `Local.research_plan`). Well-known shortcut: `Local.lastMessage` exposes the last entry of the `messages` array as a record (`Local.lastMessage.text`, `Local.lastMessage.role`).

**Opt-in at the host** — PowerFx evaluation requires the `Vais.Agents.Core.PowerFx` package:

```csharp
services.AddPowerFxExpressionEvaluator();   // registers IGraphExpressionEvaluator
```

Without this registration, an `Expression` predicate throws `InvalidOperationException` at traversal time with a message directing to `AddPowerFxExpressionEvaluator()`. The runtime container wires this automatically.

**Validation** — the manifest loader validates that `=`-prefixed strings are accepted; it does **not** type-check the PowerFx formula at load time. Formula errors surface as `InvalidOperationException` when the edge is first evaluated.

**What PowerFx can express** — boolean functions (`IsBlank`, `Not`, `And`, `Or`), comparison (`=`, `<>`, `<`, `<=`, `>`, `>=`), string functions (`StartsWith`, `EndsWith`, `Len`, `Find`), arithmetic. Numbers use `>= 3` style integer comparisons; decimal literals may require `Decimal()` or `Float()` wrappers in some locales. For cross-property comparisons, regex, or external-config lookups, prefer `HandlerRef`.

### `HandlerRef` — escape hatch

Use when the literal matcher vocabulary runs out — e.g. "compare two state properties", "regex match", "evaluate against external config":

```yaml
- from: score
  to: high-risk
  when:
    handlerRef:
      typeName: MyApp.FraudScoreThresholdPredicate
      assemblyName: MyApp
```

Implementation:

```csharp
public sealed class FraudScoreThresholdPredicate : IGraphEdgePredicate
{
    public ValueTask<bool> EvaluateAsync(
        IReadOnlyDictionary<string, JsonElement> state,
        CancellationToken cancellationToken = default)
    {
        var score = state.TryGetValue("score", out var s) ? s.GetDouble() : 0;
        var threshold = state.TryGetValue("config.threshold", out var t) ? t.GetDouble() : 0.8;
        return ValueTask.FromResult(score > threshold);
    }
}
```

Register with the orchestrator's `predicateResolver`:

```csharp
var orchestrator = new InProcessGraphOrchestrator<MyState>(
    manifest:           graphManifest,
    registry:           registry,
    lifecycle:          lifecycle,
    checkpointer:       new InMemoryCheckpointer(),
    predicateResolver:  handlerRef => handlerRef.TypeName switch
    {
        "MyApp.FraudScoreThresholdPredicate" => new FraudScoreThresholdPredicate(),
        _ => throw new InvalidOperationException($"Unknown predicate handler: {handlerRef.TypeName}"),
    });
```

Null resolver = any `HandlerRef` predicate throws at traversal time. The resolver is synchronous (returns the `IGraphEdgePredicate` instance) — the predicate's own `EvaluateAsync` is the async hook.

## Validation at manifest load

The `JsonAgentGraphManifestLoader` / `YamlAgentGraphManifestLoader` validates predicate shapes at load time — before any graph runs:

- Unknown operator names throw parse-time errors.
- `Gt` / `Gte` / `Lt` / `Lte` require a non-null `value`.
- `Exists` / `NotExists` ignore `value` (extra `value` is tolerated but unused).
- `AllOf` / `AnyOf` require a non-null (but possibly empty) list.
- `Not` requires a non-null inner predicate.
- `HandlerRef` requires a non-empty `typeName`.

Missing / invalid shapes surface in `AgentManifestValidationException.Errors` alongside any other graph-structural errors.

## .NET API shape

Everything lives in `Vais.Agents.Abstractions`:

```csharp
public abstract record GraphEdgePredicate
{
    public sealed record Always : GraphEdgePredicate;
    public sealed record PropertyMatcher(string Property, GraphPredicateOperator Operator, JsonElement? Value = null) : GraphEdgePredicate;
    public sealed record AllOf(IReadOnlyList<GraphEdgePredicate> Predicates) : GraphEdgePredicate;
    public sealed record AnyOf(IReadOnlyList<GraphEdgePredicate> Predicates) : GraphEdgePredicate;
    public sealed record Not(GraphEdgePredicate Predicate) : GraphEdgePredicate;
    public sealed record Expression(string Expr) : GraphEdgePredicate;
    public sealed record HandlerRef(GraphHandlerRef Handler) : GraphEdgePredicate;
}

public enum GraphPredicateOperator
{
    Eq, NotEq, Gt, Gte, Lt, Lte, Contains, NotContains, Exists, NotExists,
}

public interface IGraphEdgePredicate
{
    ValueTask<bool> EvaluateAsync(IReadOnlyDictionary<string, JsonElement> state, CancellationToken cancellationToken = default);
}

// In Vais.Agents.Core:
public interface IGraphExpressionEvaluator
{
    ValueTask<bool> EvaluatePredicateAsync(
        string expression,
        IReadOnlyDictionary<string, JsonElement> state,
        CancellationToken cancellationToken = default);
}

// In Vais.Agents.Core.PowerFx:
// services.AddPowerFxExpressionEvaluator();
```

Closed hierarchy — the private `GraphEdgePredicate()` ctor prevents consumer-authored subclasses. Complex predicates go through `HandlerRef`; `Expression` covers the inline-formula middle ground.

## See also

- [Graph orchestration concept](../concepts/graph-orchestration.md) — where predicates fit in the BSP super-step loop.
- [Compose an agent graph (YAML) guide](../guides/compose-an-agent-graph-yaml.md) — predicate shapes in their natural habitat.
- [Events reference](../reference/events.md) — `EdgeTraversed` event fires after predicate match + effect application.
