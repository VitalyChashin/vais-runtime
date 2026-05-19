# Intercept agent calls with an extension

You'll apply a pre-built logging extension to the runtime, watch it intercept every input and output of an existing agent without touching that agent's manifest, scope it to a single agent so it stays off others, inspect the resolved handler chain, and cleanly remove it. About 20 minutes. End state: `vais agent extensions greeter` shows the bound handlers and their scope match status; removing the extension leaves the agent unchanged.

## Why this matters

Agents are business logic — they answer questions, plan actions, summarize text. Cross-cutting concerns like logging, memory enrichment, safety guardrails, and A/B testing belong in a separate layer that can evolve independently.

Extensions are that layer. One `kind: Extension` manifest binds a handler to every `agentInput` or `agentOutput` call across as many agents as its scope rule selects — without touching any agent manifest or requiring a runtime restart. When an extension is removed, every agent it touched returns to its previous behaviour immediately.

That means:

- A logging extension added today can be pulled tomorrow with zero agent changes.
- A new guardrail can be scoped to a single agent during a trial and then promoted cluster-wide by widening the scope.
- A memory-enrichment extension can target one workspace while the rest of the fleet runs clean.

## Prerequisites

- A running runtime at `http://localhost:8080`. See [Install the runtime locally](../devops/install-the-runtime-locally.md) if you need one.
- The `greeter` agent from tutorial 1 is registered. If you skipped it, register any agent — the tutorial refers to it by name but any agent id works.
- The sample extension DLL built locally (one `dotnet build` command in Step 1).

## Step 1 — Build the sample extension

The runtime ships a tiny C# logging extension in `samples/extensions/ext-log-csharp/`. Build it:

```bash
cd agentic/samples/extensions/ext-log-csharp
dotnet build -c Release -o ./out
```

This produces `out/ext-log-csharp.dll`. The extension implements two handlers:

- `LogInput` — fires before every agent invocation; logs `[ext-log] in agent=<id> msg=<text>`.
- `LogOutput` — fires after every LLM call; logs `[ext-log] out agent=<id> tokens=<n>`.

## Step 2 — Write the extension manifest

Save as `ext-log.yaml`:

```yaml
apiVersion: vais.agents/v1
kind: Extension
metadata:
  name: ext-log
  version: "1.0.0"
  description: Logs every agent input and LLM output.
spec:
  host: csharp
  handlers:
    - id: log-input
      seam: agentInput
      priority: 900
      failureMode: log
    - id: log-output
      seam: agentOutput
      priority: 900
      failureMode: log
```

**Key fields:**

| Field | What it does |
|---|---|
| `host: csharp` | Runtime loads the DLL into a collectible ALC — no subprocess, no Docker. |
| `seam: agentInput` | Handler fires once per user message, before the agent sees it. |
| `seam: agentOutput` | Handler fires once per LLM call — including each iteration of a tool loop. |
| `priority: 900` | Higher numbers run later; 900 is a conventional "observe only" slot. |
| `failureMode: log` | A handler error is logged and the chain continues; the agent is not affected. |

No `scope` field means this extension binds cluster-wide — every agent in the runtime.

## Step 3 — Apply the extension

```bash
vais apply -f ext-log.yaml --dll ./out/ext-log-csharp.dll
```

Expected output:

```
ext-log created (version 1.0.0)
```

The runtime loaded the DLL into a fresh AssemblyLoadContext, discovered `LogInput` and `LogOutput` via the `[VaisExtension]` attribute, and registered them in the handler registry. The handlers are now active for every agent.

## Step 4 — Invoke the agent and observe the extension

```bash
vais invoke greeter --text "Hello!"
```

The agent responds normally. Meanwhile the runtime logged:

```
[ext-log] in  agent=greeter msg=Hello!
[ext-log] out agent=greeter tokens=27
```

Tail the runtime logs to see them:

```bash
vais logs greeter
```

Or for a standalone runtime container:

```bash
docker logs vais-agents-runtime --tail 20
```

The agent received no signal that an extension was bound — its manifest, state, and behaviour are unchanged.

## Step 5 — Inspect the handler chain

```bash
vais agent extensions greeter
```

```
 EXTENSION | HANDLER    | SEAM        | PRI | FAILURE | SCOPE MATCHED | SCOPE
-----------+------------+-------------+-----+---------+---------------+-----------
 ext-log   | log-input  | agentInput  | 900 | log     | yes           | cluster-wide
 ext-log   | log-output | agentOutput | 900 | log     | yes           | cluster-wide

Matched handlers are included in the agent's invocation chain.
```

`SCOPE MATCHED: yes` means the extension's scope rule accepted `greeter`. Without a `scope` field the extension is cluster-wide and always matches. The `PRI` column tells you the execution slot: lower priority values run first, so 900 runs after any guardrail at 100 or middleware at 500.

## Step 6 — Scope the extension to one agent

Cluster-wide logging is noisy in a multi-agent fleet. Narrow the scope to `greeter` only:

```yaml
spec:
  scope:
    agentIds:
      - greeter
```

Re-apply:

```bash
vais apply -f ext-log.yaml --dll ./out/ext-log-csharp.dll
```

The runtime hot-swaps the descriptor — no restart, no downtime. If you registered a second agent earlier, invoke it now and notice that its calls no longer appear in the log.

Check scope on both agents:

```bash
vais agent extensions greeter
```

```
 SCOPE MATCHED | SCOPE
 yes           | agentIds=[greeter]
```

```bash
vais agent extensions other-agent
```

```
 SCOPE MATCHED | SCOPE
 no            | agentIds=[greeter]
```

`no` means the extension is registered but excluded from that agent's chain.

## Step 7 — List all loaded extensions

```bash
vais ext list
```

```
 NAME    | VERSION | HOST   | HANDLERS
---------+---------+--------+--------------------------------
 ext-log | 1.0.0   | csharp | log-input(agentInput:900), log-output(agentOutput:900)
```

To see the full manifest and spec:

```bash
vais ext get ext-log
```

Output is YAML by default; pass `-o json` or `-o table` for other formats.

## Step 8 — Remove the extension

```bash
vais delete extension/ext-log
```

```
ext-log deleted.
```

The runtime unloads the ALC, removes both handlers from the registry, and invalidates cached agent chains. The next invocation of `greeter` runs with no extension handlers — exactly as it did before Step 3. No agent manifests were modified.

Confirm:

```bash
vais agent extensions greeter
```

```
Agent 'greeter' has no extension handlers bound.
```

## What you built

- Applied a C# extension DLL to a running runtime without restarting it.
- Observed the extension intercepting every input and per-LLM-call output.
- Narrowed the scope to a single agent with a manifest-level scope rule.
- Diagnosed handler binding with `vais agent extensions`.
- Removed the extension cleanly, restoring every agent to its original behaviour.

## Next

- **[Author an extension](../guides/author-an-extension.md)** — write your own `AgentInputMiddleware` or `AgentOutputMiddleware`, or build a Python container extension.
- [Evaluate an agent](evaluate-an-agent.md) — add an extension that injects deterministic context into test runs so eval results are reproducible.
- [Concepts → Extensions](../concepts/extensions.md) — scope evaluation order, priority collision rules, the hot-seam latency tradeoff for `host: container`.
