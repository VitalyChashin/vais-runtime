# Guide: run tools as code with code-mode

Code-mode lets an agent's LLM write a **single JavaScript program** that calls its MCP tools as
functions and composes the results — instead of issuing one JSON tool call per turn. This is the
"code execution with MCP" / CodeAct pattern: fewer round-trips, less context spent on intermediate
tool results, and real control flow (loops, filtering, conditionals) over the tool outputs.

It's **opt-in per agent** (`spec.codeMode.enabled`) and off by default. The script runs in a hardened
sidecar (a no-CLR Jint engine with timeout / statement / memory / recursion / tool-call / output
limits); the script's tool calls route back through the **same MCP gateway** as ordinary tool calls,
so governance, budgets, tracing, and per-agent tool resolution are unchanged.

> **Posture.** v1 targets *semi-trusted, first-party* code (your own governed LLM). The sandbox
> contains bugs, runaway loops, and accidental over-fetch — it is not hardened against adversarial
> untrusted code. For that, a microVM/gVisor backend is future work.

## 1. Enable code-mode on the runtime

Code-mode needs the **script-runtime sidecar** running alongside the runtime, plus two env vars on
the runtime container:

| Env var | Purpose |
|---|---|
| `VAIS_CODE_MODE_ENABLED=true` | Turns on code-mode + maps the gateway endpoints scripts call back through. |
| `VAIS_SCRIPT_RUNTIME_URL` | Where the runtime reaches the sidecar (e.g. `http://script-runtime:8080`). |
| `VAIS_INTERNAL_GATEWAY_URL` | The runtime's own address **as seen from the sidecar** (e.g. `http://runtime:8080`) — the script's tool calls post here. Not `localhost` unless the sidecar shares the runtime's network namespace. |

### Docker / docker-compose

Add the sidecar as a service and point the runtime at it:

```yaml
services:
  runtime:
    # … your existing runtime service …
    environment:
      VAIS_CODE_MODE_ENABLED:    "true"
      VAIS_SCRIPT_RUNTIME_URL:   "http://script-runtime:8080"
      VAIS_INTERNAL_GATEWAY_URL: "http://runtime:8080"

  script-runtime:
    build:
      context: ./agentic
      dockerfile: src/Vais.Agents.ScriptRuntime.Host/Dockerfile
    image: vais-script-runtime:local
    healthcheck:
      test: ["CMD", "wget", "-q", "-O-", "http://localhost:8080/health"]
      interval: 10s
      timeout: 5s
      retries: 6
```

### Kubernetes (Helm)

The chart ships a gated sidecar co-located in the runtime pod. Set:

```yaml
scriptRuntime:
  enabled: true
  image: <your-registry>/vais-script-runtime:<tag>
  port: 8090
```

When enabled, the chart injects `VAIS_CODE_MODE_ENABLED` + `VAIS_SCRIPT_RUNTIME_URL=http://localhost:<port>`
on the runtime container (the sidecar shares the pod, so `localhost` is correct there) and adds the
hardened `script-runtime` container (non-root, read-only rootfs, dropped caps, `/health` probes).

## 2. Write the agent manifest

Add `spec.codeMode` to a declarative agent and declare its tools the usual way (`mcpServers` /
`tools`). The gateway still resolves those real tools; code-mode only changes what the *LLM* sees.

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: code-mode-demo
  version: "1.0"
spec:
  handler:
    typeName: declarative
  protocols:
    - kind: Http
  model:
    provider: openai
    id: gpt-4o-mini
    apiKeyRef: secret://env/OPENAI_API_KEY
  mcpServers:
    - name: mcp-tavily
      transport: registered     # imports the registered server's tools
  codeMode:
    enabled: true
    limits:
      timeoutMs: 30000          # wall-clock cap per script
      maxToolCalls: 6           # tool calls one script may make
      maxOutputBytes: 8192      # cap on the result returned to the model
  systemPrompt:
    inline: |
      You have a single tool, run_code, plus a generated JavaScript API over your MCP tools
      (documented in the run_code description). Prefer ONE script that does all the work: call
      tools[ "name" ](args) per item, collect results into variables, and `return` a combined
      plain-text summary. After that one run_code call returns, write your final answer.
  budget:
    maxTurns: 12                # see "Budget for the run_code loop" below
```

`spec.codeMode` fields: `enabled` (bool), `runtime` (`jint`, the default), `generator` (`raw`, the
default), `toolset` (optional list narrowing which tools are exposed; omitted = all the agent's
tools), and `limits` (the caps above plus `maxStatements` and `recursionDepth`).

## 3. Apply and invoke

```bash
vais apply -f code-mode-demo.yaml
vais invoke code-mode-demo --text "Compare two landmarks, one web search each: the Eiffel Tower and Mount Fuji. Return one sentence."
```

The agent writes a script that searches for each landmark and returns a combined answer — composed
in a single `run_code` call rather than two separate tool-call turns.

## What happens per turn

1. The LLM is shown **one** tool, `run_code`, plus a generated JS API — `tools["<name>"](args)` — over
   the agent's tools, documented in the tool description.
2. It writes a script; the runtime ships `{ prelude + script }` to the sidecar with a short-turn
   **call token** scoped to this run.
3. Inside the sidecar, each `tools.*` call compiles to one bridge call that posts to the runtime's
   `/v1/container-gateway/tools/invoke` — the **same** path container/Python plugins use. The full
   tool-gateway middleware chain (auth, budgets, rate-limit, Langfuse tracing) runs unchanged, and
   `ResolveAgentToolsAsync` resolves the agent's real tools.
4. The script's return value comes back as the `run_code` result; the agent loop continues.

A script failure (timeout, limit breach, a tool error, or a script exception) is returned as a
**classified error**, surfaced to the model as a real tool failure — never a fabricated success.

## Budget for the run_code loop

Each `run_code` call is one agent turn. A model may call it more than once (refining, re-searching),
so set `budget.maxTurns` with headroom and prompt it to do the work in a single script. If you see
`RunBudget.MaxTurns exceeded`, raise `maxTurns` — the mechanism is working; the model just iterated.

## v1 limitations

- **Semi-trusted only** (see the posture note above).
- **JavaScript only** (Jint). Python / WASM backends are future work.
- **Scripts can't `try/catch` tool errors in-JS** — a failed tool call aborts the script and returns
  a classified error to the model. (Catchable in-script errors are deferred.)
- **No filesystem or network** inside the script beyond the injected tool bridge — that's the point.

## See also

- [Expose MCP tools to an agent](expose-mcp-tools-to-an-agent.md) — the ordinary (per-call) way to give an agent tools.
- [Concept → Tools](../concepts/tools.md) — `ITool`, `IToolRegistry`, the gateway dispatch path.
- [Reference → Manifest schema](../reference/manifest-schema.md) — `spec.codeMode` field reference.
- Sample: `samples/PluginAgentResearchPipeline/agents/code-mode-demo.yaml`.
