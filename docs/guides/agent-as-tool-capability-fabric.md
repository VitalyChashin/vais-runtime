# Agent-as-tool capability fabric

When a coordinator agent delegates to sub-agents (via `AgentManifest.LocalAgents` + `ToolRef.Source = "agent:..."`), the **capability fabric** gives the coordinator ontology-aware context about its team and lets the deployer govern delegations at the boundary. The fabric reuses the SEP-1763 substrate (see [docs/concepts/ontology-substrate.md](../concepts/ontology-substrate.md)) — no new dispatch plumbing; the existing `LocalAgentTool` depth guard and `AgentContext.AllowedTools` enforcement stay as-is, and the fabric layers descriptive + advisory + validation on top.

This guide covers:

1. The **capability map** — what it is, how to inject it into the coordinator's prompt.
2. **Ontology-driven `AllowedTools`** — scoping a coordinator to specific sub-agents by capability tags.
3. **Delegation governance** — denying / requiring-precondition / cost-guarding sub-agent calls.
4. The **§14.5 boundary** — what the fabric is *not*: a sequencer.

The fabric is OSS; per-deployment policy *content* (which sub-agent a role may delegate to, what preconditions are required) stays deployment-local.

## The pieces

| Type | Project | Role |
|---|---|---|
| `IAgentCapabilityMapBuilder` / `AgentCapabilityMapBuilder` | `Vais.Agents.Control.Manifests.Json` | Builds a per-coordinator `CapabilityMap` from `IAgentRegistry` + manifest cross-join. |
| `CapabilityMap` / `SubAgentCapability` | `Vais.Agents.Control.Manifests.Json` | The "your team" view; `ToCompactText()` renders the compact in-band block. |
| `CapabilityMapInputMiddleware` | `Vais.Agents.Control.Manifests.Json` | `AgentInputMiddleware` that injects the map into the coordinator's context. |
| `IOntologyAllowedToolsResolver` / `OntologyAllowedToolsResolver` | `Vais.Agents.Control.Manifests.Json` | Computes `AllowedTools` from caller scopes ∩ sub-agent tags. |
| `IDelegationPolicy` / `DelegationGovernanceMiddleware` | `Vais.Agents.Control.Manifests.Json` | Request-phase `ToolGatewayMiddleware` that runs the policy against agent-as-tool calls. |
| `AllowAllDelegationPolicy` | `Vais.Agents.Control.Manifests.Json` | OSS default — allow every delegation; deployers override with their policy. |

## 1. The capability map

A coordinator's capability map is built from its registered manifest. Two channels:

- `manifest.LocalAgents: IReadOnlyList<LocalAgentRef>?` — the bindings (each `LocalAgentRef` carries `Name`, optional `AgentId`, `Description`, `Mode`).
- `manifest.Tools: IReadOnlyList<ToolRef>` — entries with `Source = "agent:<LocalAgentRef.Name>"` are the LLM-visible tool names.

The builder cross-joins these and pulls each target agent's `Description` + `Labels` to produce one `SubAgentCapability` per delegate-able tool:

```csharp
using Vais.Agents.Control.Manifests;

var services = new ServiceCollection();
services.AddSingleton<IAgentCapabilityMapBuilder, AgentCapabilityMapBuilder>();

var sp = services.BuildServiceProvider();
var builder = sp.GetRequiredService<IAgentCapabilityMapBuilder>();
var map = await builder.BuildAsync("my-coordinator");

// map.SubAgents = [
//   SubAgentCapability("reviewer", "code-reviewer", "Reviews code diffs.", ["role:review"], Blocking),
//   SubAgentCapability("deployer", "deploy-bot",   "Deploys to dev.",     ["role:ops", "risk:Destructive"], Blocking),
// ]
```

`ToCompactText()` renders the in-band block:

```text
Your team (delegate by calling the tool by name):
- reviewer: Reviews code diffs. [role:review]
- deployer: Deploys to dev. [role:ops, risk:Destructive]
```

The map is cached per coordinator id; call `Invalidate(id)` after a manifest change.

## 2. Inject the map into the coordinator's context

`CapabilityMapInputMiddleware` is an `AgentInputMiddleware` that runs before each turn. It writes the structured map into `AgentInputContext.Properties["vais.capability_map"]` and prepends the compact text onto `AgentInputContext.Message` (in-band by default, so any existing agent picks it up without code change).

```csharp
services.AddSingleton(sp => new CapabilityMapInputMiddleware(
    sp.GetRequiredService<IAgentCapabilityMapBuilder>(),
    new CapabilityMapInputMiddlewareOptions { InjectIntoMessage = true }));
```

The deployer wires the middleware as an Extension scoped to coordinator agents (see [docs/guides/author-an-extension.md](author-an-extension.md)). With `InjectIntoMessage = false` the structured map is still set in `Properties`; the user message stays clean — useful when the coordinator has its own programmatic consumer.

## 3. Ontology-driven `AllowedTools`

`OntologyAllowedToolsResolver` computes which sub-agents a caller is allowed to delegate to, based on the caller's scopes intersected with each sub-agent's tags:

```csharp
var resolver = new OntologyAllowedToolsResolver();
var allowed = resolver.Compute(callerScopes: ["role:reviewer"], map);
// allowed = ["reviewer"]   (untagged sub-agents would also be in this set)
```

Tag-intersection policy:

| Sub-agent state | Caller scopes | Outcome |
|---|---|---|
| Untagged | (any) | Allowed — deployer must tag sub-agents to restrict. |
| Tagged | Contains at least one matching tag | Allowed. |
| Tagged | No matching tags | Denied. |
| Tagged | `"*"` wildcard scope present | Allowed (configurable via `WildcardScope`). |
| Tagged | Empty / null caller scopes | `GrantOnEmptyScope` (default `true`) ⇒ allowed (dev posture). Set to `false` for strict multi-tenant deny-by-default. |

Pipe the resolver's output into `AgentContext.AllowedTools` and the existing `DefaultToolCallDispatcher` enforces it — no new enforcement code.

## 4. Delegation governance

`DelegationGovernanceMiddleware` is a `ToolGatewayMiddleware` (Kind = Validation) that runs `IDelegationPolicy` against every tool call whose name matches a sub-agent in the coordinator's `CapabilityMap`. Regular tool calls (MCP, static, A2A) pass through unchanged.

```csharp
// Deployer-supplied policy
public sealed class ReviewBeforeDeployPolicy : IDelegationPolicy
{
    public ValueTask<DelegationDecision> EvaluateAsync(
        ToolGatewayContext ctx, CapabilityMap map, CancellationToken ct = default)
    {
        if (ctx.ToolName == "deployer" && !RunHasPriorReviewerSuccess(ctx))
            return new(DelegationDecision.Deny(
                "deployer requires prior reviewer approval in this run",
                ["call 'reviewer' first; deploy only after it returns ok:true"]));
        return new(DelegationDecision.Allow);
    }

    private bool RunHasPriorReviewerSuccess(ToolGatewayContext ctx) { /* deployer tracks */ }
}

services.AddSingleton<IDelegationPolicy, ReviewBeforeDeployPolicy>();
services.AddSingleton<ToolGatewayMiddleware>(sp => new DelegationGovernanceMiddleware(
    sp.GetRequiredService<IDelegationPolicy>(),
    sp.GetRequiredService<IAgentCapabilityMapBuilder>()));
```

On deny, the middleware short-circuits with a structured tool-call outcome:

```json
{
  "ok": false,
  "reason": "deployer requires prior reviewer approval in this run",
  "suggestions": ["call 'reviewer' first; deploy only after it returns ok:true"]
}
```

The LLM sees a normal tool-call failure and can adapt — this is **not** a turn abort. Consistent with the existing `LocalAgentTool` depth-guard pattern.

### What about cost guards and cycle detection?

The policy is responsible for any per-run history tracking (preconditions, invocation counters, cycle detection beyond the existing depth guard). The OSS default `AllowAllDelegationPolicy` is a no-op so the middleware stays advisory until you write a policy.

### Default policy

Register `AllowAllDelegationPolicy.Instance` as the OSS default so the middleware is harmless if added without a custom policy:

```csharp
services.TryAddSingleton<IDelegationPolicy>(_ => AllowAllDelegationPolicy.Instance);
```

## 5. §14.5 — the ontology is **not** a sequencer

The capability fabric stays **descriptive + advisory + validation**. It surfaces information ("your team", recipes) and validates calls (preconditions, scope intersection). It **never** auto-executes a sequence of delegations.

The moment something encodes "A then B then C" deterministically, that's a **Graph** (P7), not the ontology. Graphs already exist in the runtime (`AgentGraph` kind) and are the right tool for deterministic multi-step orchestration.

The guard tests in `Vais.Agents.Core.Tests.Ontology.CapabilityFabricInvariantsTests` pin this contract:

- `CapabilityMapInputMiddleware` never calls a tool / agent / runtime — it only mutates `Properties` + `Message`.
- The `Vais.Agents.Control.Manifests.Json` assembly exposes no public type matching sequencer / auto-executor patterns (`Sequencer`, `AutoExecutor`, `RecipeExecutor`, `AutoDelegator`).
- `OntologyOverlay.RecipeEntry` is a data-only carrier — no `Execute` / `Run` / `Invoke` methods, no execution-shaped interfaces.

If you find yourself wanting to add a sequencer here, build a graph instead.

## Composition with the south cartridge

The fabric and the south cartridge ([attach-a-domain-ontology.md](attach-a-domain-ontology.md)) compose: a coordinator with delegation governance + sub-agents that each have their own `McpServer.OntologyRef`-bound cartridges produce a **capability fabric at every boundary** (research §14.3, principle P7). Each level applies its own ontology; per-agent translator caching + child-session isolation (P8) keep them independent.

## Related

- [docs/concepts/ontology-substrate.md](../concepts/ontology-substrate.md) — SEP-1763 substrate that powers both the south cartridge and this fabric.
- [docs/guides/attach-a-domain-ontology.md](attach-a-domain-ontology.md) — the south cartridge for shaping MCP tool surfaces (composes with this fabric).
- [docs/guides/delegate-to-a-local-agent.md](delegate-to-a-local-agent.md) — the underlying agent-as-tool mechanism the fabric layers on.
- [docs/guides/author-an-extension.md](author-an-extension.md) — how to scope the capability-map middleware to specific coordinator agents.
