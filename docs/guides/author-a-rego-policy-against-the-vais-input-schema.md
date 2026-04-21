# Guide: author a Rego policy against the VAIS input schema

Four reusable guard patterns for Rego policies that gate `Vais.Agents` verbs via `Vais.Agents.Control.Policy.Opa`. Each pattern answers a concrete class of "deny this" question; composing them covers the common platform-team scenarios. Starter files for three of the four ship in `samples/opa-policies/`.

Prereqs: familiarity with Rego basics (packages, rules, `input`, `data`). [OPA's documentation](https://www.openpolicyagent.org/docs/) is authoritative; this guide is the `vais-agents`-specific wrinkles on top.

## The input shape — one-line reminder

Every `POST /v1/data/{DataPath}` body the engine sends carries:

```json
{ "input": {
    "schemaVersion": "1",
    "operation":     "Create" | "Invoke" | "Signal" | "Query" | "Cancel" | "Update" | "Evict",
    "principal":     { "id", "tenantId", "scopes"? } | null,
    "agent":         <full AgentManifest in camelCase>  | null
} }
```

`contracts/opa-input-schema.md` is the authoritative field reference — consult it when a specific field's type matters.

## Pattern 1: null-principal guard

`input.principal` is `null` for unauthenticated callers. Rego's null handling is loose — unguarded field access on `null` silently fails to undefined (Rego doesn't throw), which means naïve rules **allow** cross-tenant anonymous access unless you check explicitly.

```rego
package vais.agents

default allow := {"allowed": false, "reason": "default-deny — principal missing"}

# Explicit null check first — fail closed on anonymous callers.
allow := {"allowed": false, "reason": "unauthenticated caller"} if {
    input.principal == null
}

# … then your allow rules, each of which can safely read input.principal.*
allow := {"allowed": true} if {
    input.principal != null
    input.principal.scopes[_] == "admin"
}
```

**Why this matters:** without the explicit null check, `input.principal.scopes[_] == "admin"` evaluates to `undefined` when `principal` is null, which Rego treats as "rule didn't fire" — the default-deny holds. Explicit guard makes the deny **loud**: the audit log carries `"unauthenticated caller"` instead of a generic default-deny reason.

## Pattern 2: null-agent guard

`input.agent` is `null` on `Query` against an agent id not in the registry. Same hygiene as Pattern 1 — if your rule reads `input.agent.labels.tenant`, you need a prior guard:

```rego
allow := {"allowed": false, "reason": "agent not found"} if {
    input.operation == "Query"
    input.agent == null
}

allow := {"allowed": true} if {
    input.operation == "Query"
    input.agent != null
    input.agent.labels.tenant == input.principal.tenantId
}
```

**Why this matters:** `Query` on a non-existent agent still consults the policy engine — the runtime needs a `Allow` / `Deny` to decide whether to return `404` or `403`. Without the null guard, the policy silently denies with a "default-deny" reason that obscures what happened. The explicit guard lights up the audit trail.

## Pattern 3: operation gate

Branch on `input.operation` to gate different verbs differently. Typical platform-team setup: tight for Invoke/Signal/Query (business calls), loose for Create/Update/Evict (platform lifecycle):

```rego
package vais.agents

# Business verbs — gated on tenant match.
gated_operation if { input.operation == "Invoke" }
gated_operation if { input.operation == "Signal" }
gated_operation if { input.operation == "Query" }

default allow := {"allowed": true}   # platform-team verbs fall through

allow := {"allowed": true} if {
    gated_operation
    input.principal != null
    input.agent != null
    input.agent.labels.tenant == input.principal.tenantId
}

allow := {"allowed": false, "reason": "cross-tenant"} if {
    gated_operation
    input.principal != null
    input.agent != null
    input.agent.labels.tenant != input.principal.tenantId
}
```

The seven operations on `IAgentLifecycleManager`:

| Operation | Typical gate |
|---|---|
| `Create` | Platform admin scope; model-provider allowlist; budget caps. |
| `Update` | Same as Create. |
| `Invoke` | Tenant match; quota; feature flag. |
| `Signal` | Same as Invoke — it's a mid-run callback. |
| `Query` | Tenant match; read-only so often laxer than Invoke. |
| `Cancel` | Invoke's inverse — usually same gate. |
| `Evict` | Platform admin scope. |

You can gate any subset. Un-gated verbs fall through the `default allow` rule. See `samples/opa-policies/tenant-scoped-allow.rego` for the canonical business-verbs-only gate.

## Pattern 4: multi-rule compose

OPA's data-join semantics let you compose multiple gates (tenant + model allowlist + budget cap) cleanly — split each concern into its own package, then a top-level `allow` that fails fast on any deny:

```rego
# tenant.rego
package vais.agents.tenant

default allow := {"allowed": true}

allow := {"allowed": false, "reason": "cross-tenant"} if {
    input.operation in {"Invoke", "Signal", "Query"}
    input.principal != null
    input.agent != null
    input.agent.labels.tenant != input.principal.tenantId
}
```

```rego
# models.rego
package vais.agents.models

default allowed_providers := {"openai", "anthropic", "azureOpenAi"}
default allow := {"allowed": true}

allow := {"allowed": false, "reason": "model provider not in allowlist"} if {
    input.operation in {"Create", "Update"}
    input.agent != null
    input.agent.model != null
    not allowed_providers[input.agent.model.provider]
}
```

```rego
# budgets.rego
package vais.agents.budgets

default allow := {"allowed": true}

allow := {"allowed": false, "reason": "maxTurns exceeds cap"} if {
    input.operation in {"Create", "Update"}
    input.agent != null
    input.agent.budget != null
    input.agent.budget.maxTurns > 20
}
```

```rego
# main.rego — the adapter's entry point
package vais.agents

import data.vais.agents.tenant as tenant
import data.vais.agents.models as models
import data.vais.agents.budgets as budgets

default allow := {"allowed": true}

# Short-circuit on first deny.
allow := denial if {
    denial := tenant.allow
    denial.allowed == false
}
allow := denial if {
    denial := models.allow
    denial.allowed == false
}
allow := denial if {
    denial := budgets.allow
    denial.allowed == false
}
```

The adapter queries `data.vais.agents.allow` (from the default `DataPath`). Each sub-policy returns an `{"allowed", "reason"}` object; `main.rego` picks the first denial and propagates it. All allows → the default `allow: {"allowed": true}` fires.

**Why short-circuit over AND-ing all three:** the `reason` field is informative. A cross-tenant denial says `"cross-tenant"`; a budget denial says `"maxTurns exceeds cap"`. Short-circuit preserves the specific reason that tripped. Combining with `all.allowed == false` would lose that detail.

## Testing locally with `opa eval`

Save a sample input as `input.json`:

```json
{
  "input": {
    "schemaVersion": "1",
    "operation":     "Invoke",
    "principal":     { "id": "alice", "tenantId": "tenant-a" },
    "agent": {
      "id":       "weather",
      "version":  "1.0",
      "handler":  { "typeName": "MyApp.WeatherAgent" },
      "protocols": [{"kind": "Http"}],
      "tools":    [],
      "labels":   { "tenant": "tenant-a" }
    }
  }
}
```

Evaluate:

```bash
opa eval --data tenant-scoped-allow.rego --input input.json 'data.vais.agents.allow'
# {
#   "result": [{ "expressions": [{ "value": {"allowed": true}, "text": "data.vais.agents.allow", "location": {...} }] }]
# }
```

Swap the tenant id to mismatch and re-run — the result flips to `{"allowed": false, "reason": "cross-tenant access denied"}`.

## Unit-testing with `opa test`

Each Rego file can ship a `_test.rego` sibling:

```rego
# tenant-scoped-allow_test.rego
package vais.agents

test_same_tenant_allowed if {
    result := allow with input as {
        "schemaVersion": "1",
        "operation":     "Invoke",
        "principal":     { "id": "alice", "tenantId": "tenant-a" },
        "agent":         { "id": "weather", "labels": { "tenant": "tenant-a" } }
    }
    result == {"allowed": true}
}

test_cross_tenant_denied if {
    result := allow with input as {
        "schemaVersion": "1",
        "operation":     "Invoke",
        "principal":     { "id": "bob", "tenantId": "tenant-b" },
        "agent":         { "id": "weather", "labels": { "tenant": "tenant-a" } }
    }
    result.allowed == false
}
```

Run:

```bash
opa test tenant-scoped-allow.rego tenant-scoped-allow_test.rego
# PASS: 2/2
```

Ship the tests as part of the policy bundle; wire into CI as a gate on Rego PRs.

## Common mistakes

- **Omitting the null-principal / null-agent guards** — policy silently defaults to "hidden deny" and the audit log can't explain why. Explicit guards light up the reason.
- **Using `schemaVersion` as a number** — it's a string (`"1"`, not `1`). `input.schemaVersion == 1` is always false.
- **Expecting `allow` to be a boolean** — the adapter accepts both bool and object shapes, but if you return `true` instead of `{"allowed": true}` the deny path has no `reason` to log. Stick with the object shape.
- **Forgetting that `Create` on a first-time agent has `input.agent` populated from the request** — it's not null. A `default-deny on principal-null` policy still allows `Create` unless you explicitly gate it.
- **Policy-reload latency.** The adapter's decision cache (default `DecisionCacheTtl = 5s`) overlays any OPA hot-reload. Expect up to 5 seconds of old-policy decisions after you flip the Rego.

## See also

- [OPA policy engine concept](../concepts/opa-policy-engine.md) — FailMode, caching, wire contract.
- [Gate agents with OPA](gate-agents-with-opa.md) — end-to-end host wiring.
- [Wire a sidecar OPA against the operator](wire-a-sidecar-opa-against-the-operator.md) — K8s deployment.
- `contracts/opa-input-schema.md` — authoritative input-shape reference.
- `samples/opa-policies/tenant-scoped-allow.rego` / `model-provider-allowlist.rego` / `budget-cap.rego` — copy-paste starters.
- [OPA language documentation](https://www.openpolicyagent.org/docs/) — external, Rego fundamentals.
