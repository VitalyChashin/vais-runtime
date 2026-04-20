# Rego policy samples for Vais.Agents.Control.Policy.Opa

Three sample Rego policies you can mount into an OPA sidecar to gate the
v0.6 `IAgentLifecycleManager` verbs. They assume the shipped v1 input
schema documented at
[`contracts/opa-input-schema.md`](../../contracts/opa-input-schema.md).

| Sample | Gates | Use when |
|---|---|---|
| [`tenant-scoped-allow.rego`](tenant-scoped-allow.rego) | Invoke / Signal / Query | Multi-tenant cluster; deny cross-tenant access to an agent |
| [`model-provider-allowlist.rego`](model-provider-allowlist.rego) | Create / Update | Platform team controls approved LLM vendors |
| [`budget-cap.rego`](budget-cap.rego) | Create / Update | Deny manifests whose run-budget fields exceed operator caps |

## Installing

All three policies declare `package vais.agents` and expose an `allow`
rule at that path — matching the adapter default
`OpaPolicyEngineOptions.DataPath = "vais/agents/allow"`.

Pick whichever combination fits, concatenate into a single `.rego` file
(or load as separate files — OPA composes), and mount into your OPA
sidecar. See [`../opa-sidecar/README.md`](../opa-sidecar/README.md) for
the ConfigMap + Helm overlay pattern.

## Composing multiple rules

The adapter queries a single path (default `vais/agents/allow`). If you
want to combine gates, write an outer rule that `or`s the individual
checks:

```rego
package vais.agents

import data.vais.agents.tenant as tenant_check
import data.vais.agents.models as model_check
import data.vais.agents.budgets as budget_check

default allow := {"allowed": true}

allow := result if {
    result := tenant_check.allow
    result.allowed == false
}
allow := result if {
    result := model_check.allow
    result.allowed == false
}
# ...etc
```

(Split the three sample files into separate packages and import them.)

## Response shapes

The adapter accepts both `allow = true/false` (bool) and
`allow = {"allowed": ..., "reason": "..."}` (object). The samples use
the object shape so denials carry a human-readable reason back to the
audit log. The object fields are:

- `allowed` (bool, required)
- `reason` (string, optional — defaults to `"Policy denied"` when omitted)

## Testing locally

```bash
# Save one of the samples as policy.rego, then:
opa eval --data policy.rego --input input.json 'data.vais.agents.allow'
```

Where `input.json` contains a representative
[`contracts/opa-input-schema.md`](../../contracts/opa-input-schema.md)
payload.
