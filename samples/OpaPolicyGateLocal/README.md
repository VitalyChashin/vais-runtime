# OpaPolicyGateLocal

Gate agent creates on an OPA model-provider allowlist. Two `CreateAsync` calls are sent — one with an allowed provider (`openai`) and one with a denied provider (`blocked-llm`). The first succeeds; the second throws `AgentPolicyDeniedException`. Both calls emit an audit-log entry via `LoggerAuditLog`.

## Prerequisites

Install the OPA CLI and start it with the bundled policy:

```bash
# from the repo root
opa run --server samples/OpaPolicyGateLocal/policy.rego
```

Download OPA: <https://www.openpolicyagent.org/docs/latest/#running-opa>

## Run

```bash
dotnet run --project samples/OpaPolicyGateLocal
```

## Expected output

```
OPA:   http://localhost:8181  ✓

== create 1 — openai provider (allowed) ==
info: Vais.Agents.Control.InProcess.LoggerAuditLog[0]
      AgentControlPlane audit: Create agent=agent-openai version=1.0 principal=(null) tenant=(null) allowed=True denyReason=(null) errorType=(null)
  result: allowed ✓

== create 2 — blocked-llm provider (denied) ==
warn: Vais.Agents.Control.InProcess.LoggerAuditLog[0]
      AgentControlPlane audit: Create agent=agent-blocked version=1.0 principal=(null) tenant=(null) allowed=False denyReason=model provider not in allowlist errorType=(null)
  result: denied ✓  operation=Create  reason="model provider not in allowlist"

Done.
```

## What it demonstrates

- `AddOpaPolicyEngine(opts => ...)` — registers a typed `HttpClient` + `IAgentPolicyEngine` in DI. `BaseUrl` points to the local OPA process; `FailMode = Closed` denies all calls when OPA is unreachable.
- `AddSingleton<IAuditLog, LoggerAuditLog>()` — routes every lifecycle-verb audit event to the host `ILogger` under category `Vais.Agents.Control.InProcess.LoggerAuditLog`. Pairs with any log aggregator (OTel, Seq, ELK) in production.
- `AgentLifecycleManager(registry, runtime, policy: policy, audit: audit)` — optional `IAgentPolicyEngine` and `IAuditLog` wired via named parameters; the manager evaluates policy before executing every lifecycle verb.
- `AgentPolicyDeniedException` — thrown by `CreateAsync` when OPA returns `{"allowed": false}`. Carries `Operation` (the `PolicyOperation` enum value) and `Reason` (the string from the Rego rule).
- `policy.rego` — `package vais.agents` with a hard-coded `allowed_providers` set (`openai`, `anthropic`, `azureOpenAi`). Denies `Create` / `Update` when `input.agent.model.provider` isn't in the set. The allowlist can be overridden at OPA startup via a `data.json` file or bundle without changing the Rego source.
- `OpaFailMode.Closed` — enterprise-safe default: when OPA is unreachable the engine emits a deny so production traffic is never silently allowed through a broken policy sidecar.

## Policy shape

The OPA policy receives this input on every `CreateAsync`:

```json
{
  "operation": "Create",
  "agent": {
    "id": "agent-blocked",
    "model": { "provider": "blocked-llm", "id": "proprietary-v1" }
  }
}
```

The `allow` rule in `policy.rego` returns `{"allowed": false, "reason": "model provider not in allowlist"}` when `provider` is not in the allowlist.

## Docs

- [Policy](../../docs/concepts/policy.md)
- [`AgentGraphInProcess`](../AgentGraphInProcess) — graph orchestration (next step after control plane)
