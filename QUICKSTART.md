# Quickstart — declarative agents on the Vais.Agents runtime

Go from `git clone` to a multi-agent graph that fetches web content through observable LLM + MCP gateways in **~15 minutes**. Everything below is declarative YAML — no C# changes required.

By the end you will have:

- The `vais-agents-runtime` container running locally on `:8080`.
- A public `mcp-fetch` server registered as a tool source.
- An `LlmGatewayConfig` and an `McpGatewayConfig` wrapping every LLM call and every tool call with structured logging, OpenTelemetry, rate limiting, and response truncation.
- Three declarative agents — `planner`, `researcher`, `reporter` — wired into an `AgentGraph` that runs end-to-end on a single user query.
- (Optional bonus) one node swapped for a Python plugin agent.

```
              ┌─────────┐         ┌────────────┐         ┌──────────┐
   query ───► │ planner │ ──────► │ researcher │ ──────► │ reporter │ ──► end
              └─────────┘         └─────┬──────┘         └──────────┘
                                        │ fetch tool
                                        ▼  (via MCP gateway)
                                  ┌──────────────┐
                                  │  mcp-fetch   │
                                  └──────────────┘
```

> **Status.** Targets v0.16 of the runtime and v0.15 of the CLI. API is pre-alpha — surfaces may move.

---

## Prerequisites

| Tool | Version | Why |
|---|---|---|
| Docker Engine / Docker Desktop | 24+ / 4.30+ | Runtime + MCP fetch sidecar |
| .NET 9 SDK | 9.0+ | `vais` CLI is a global `dotnet tool` |
| An OpenAI API key | — | Agents call `gpt-4o-mini` |

Set the key in your shell.

```bash
export OPENAI_API_KEY=sk-...
```

<details><summary>PowerShell</summary>

```powershell
$env:OPENAI_API_KEY = "sk-..."
```

</details>

Clone and enter the repo.

```bash
git clone https://github.com/VitalyChashin/vais-runtime.git
cd <repo>/agentic
```

---

## 1. Start the runtime and the MCP fetch sidecar (3 min)

Build the runtime image (first time ~2 min; cached ~20 s).

```bash
docker build -f src/Vais.Agents.Runtime.Host/Dockerfile -t vais-agents-runtime:local .
```

Create a working directory.

```bash
mkdir -p ~/quickstart
cd ~/quickstart
```

<details><summary>PowerShell</summary>

```powershell
New-Item -ItemType Directory -Force -Path "$HOME/quickstart"
Set-Location "$HOME/quickstart"
```

</details>

Save this as `docker-compose.yaml` in `~/quickstart/` — it bundles the runtime, the MCP fetch server, and the wiring needed for **container-plugin supervision** (step 6).

```yaml
services:
  runtime:
    image: vais-agents-runtime:local
    container_name: vais-runtime
    environment:
      VAIS_HOSTING_MODE: localhost
      ASPNETCORE_URLS: http://0.0.0.0:8080
      OPENAI_API_KEY: ${OPENAI_API_KEY}
    ports:
      - "8080:8080"
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock             # lets the runtime spawn plugin containers
    depends_on:
      mcp-fetch:
        condition: service_healthy
    healthcheck:
      test: ["CMD", "wget", "-qO-", "http://localhost:8080/healthz"]
      interval: 10s
      timeout: 3s
      retries: 5
      start_period: 15s

  mcp-fetch:
    image: ghcr.io/modelcontextprotocol/servers/fetch:latest
    container_name: mcp-fetch
    environment:
      PORT: "3000"
    ports:
      - "3000:3000"
    healthcheck:
      test: ["CMD", "wget", "-qO-", "http://localhost:3000/health"]
      interval: 10s
      timeout: 3s
      retries: 5
      start_period: 10s
```

> **Why mount `docker.sock`?** Step 6 deploys a Python agent as its own Docker container managed by the runtime's `DockerContainerSupervisor`. The supervisor uses the Docker API via the mounted socket to start, stop, and replace plugin containers — no runtime image rebuild, no silo restart. If you have no intention of running container plugins, the socket mount can be removed.

Start both containers.

```bash
docker compose up -d
```

Verify both are healthy.

```bash
curl -s http://localhost:8080/healthz   # → ok
curl -s http://localhost:3000/health    # → ok
```

<details><summary>PowerShell</summary>

```powershell
curl.exe -s http://localhost:8080/healthz
curl.exe -s http://localhost:3000/health
```

The PowerShell `curl` alias points at `Invoke-WebRequest`; `curl.exe` is the real client and ships with Windows 10+.

</details>

---

## 2. Install and point the CLI (1 min)

```bash
dotnet tool install -g Vais.Agents.Cli --version 0.15.0-preview
vais version                                            # → 0.15.0-preview

vais config set-context local --server http://localhost:8080
vais config use-context local
vais get agents                                         # → No agents registered.
```

If `vais` is not on PATH, add `~/.dotnet/tools` to your shell profile (Linux/macOS) or `%USERPROFILE%\.dotnet\tools` to the user PATH (Windows).

---

## 3. Apply the gateway configs and register the MCP server (2 min)

The runtime validates `llmGatewayRef`, `mcpGatewayRef`, and `mcpServers[*].name` **eagerly** when an agent is applied — so gateways and servers must exist first.

Save these three files in a working directory (e.g. `~/quickstart/`).

**`llm-gateway.yaml`** — structured logging, OTel spans, and Prometheus counters on every LLM call.

```yaml
apiVersion: vais.agents/v1
kind: LlmGatewayConfig
metadata:
  id: demo-llm-gateway
  version: "1.0"
  description: Observable LLM gateway — logging, OTel, Prometheus.
spec:
  middleware:
    - name: LlmLogging
    - name: LlmOtel
    - name: Prometheus
```

**`mcp-gateway.yaml`** — OTel traces, 30 calls/minute rate limit, response truncation on every tool call.

```yaml
apiVersion: vais.agents/v1
kind: McpGatewayConfig
metadata:
  id: demo-mcp-governance
  version: "1.0"
  description: Tool governance — OTel, rate limit, truncation.
spec:
  middleware:
    - name: ToolOtel
    - name: ToolRateLimit
      params:
        callsPerMinute: 30
    - name: ToolResponseTruncation
      params:
        maxCharacters: 8192
```

**`mcp-fetch-server.yaml`** — register the public fetch server as a reusable tool source.

```yaml
apiVersion: vais.agents/v1
kind: McpServer
metadata:
  id: mcp-fetch
  version: "1.0"
  description: HTTP fetch tool via MCP (streamableHttp transport).
spec:
  transport: streamableHttp
  url: http://host.docker.internal:3000/mcp
  mcpGatewayRef: demo-mcp-governance
```

> **Note.** `host.docker.internal` resolves to the host from inside the runtime container on Docker Desktop. On Linux without Docker Desktop, use the container's host-network address or join the fetch container to a shared user-defined bridge and address it by name.

Apply in dependency order.

```bash
vais apply -f llm-gateway.yaml
vais apply -f mcp-gateway.yaml
vais apply -f mcp-fetch-server.yaml
```

Each command prints `applied <Kind> <id>@<version>`.

---

## 4. Deploy three declarative agents (3 min)

All three agents reference the same gateway configs. Only `researcher` is granted the fetch tool — keeping the LLM-only nodes off the tool plane.

**`planner.yaml`**

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: planner
  version: "1.0"
  description: Decomposes a query into 3 focused sub-questions.
spec:
  model:
    provider: openai
    name: gpt-4o-mini
  systemPrompt:
    inline: |
      You decompose a user research query into exactly three sub-questions
      that together cover the topic. Reply with three lines, no preamble,
      one sub-question per line.
  handler:
    typeName: declarative
  protocols:
    - kind: Http
  llmGatewayRef: demo-llm-gateway
  tools: []
  budget:
    maxTurns: 1
```

**`researcher.yaml`** — bound to both gateways, with the `fetch` tool sourced from `mcp:mcp-fetch`.

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: researcher
  version: "1.0"
  description: Fetches web content for each sub-question and summarises findings.
spec:
  model:
    provider: openai
    name: gpt-4o-mini
  systemPrompt:
    inline: |
      For each sub-question you receive, call the `fetch` tool exactly once
      with a relevant URL, then summarise what you found in 2-3 sentences.
      Cite the URL you fetched. Be concise.
  handler:
    typeName: declarative
  protocols:
    - kind: Http
  llmGatewayRef: demo-llm-gateway
  mcpGatewayRef: demo-mcp-governance
  mcpServers:
    - name: mcp-fetch
      transport: registered
  tools:
    - name: fetch
      source: mcp:mcp-fetch
  budget:
    maxTurns: 5
```

**`reporter.yaml`**

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: reporter
  version: "1.0"
  description: Composes the final report from the assembled findings.
spec:
  model:
    provider: openai
    name: gpt-4o-mini
  systemPrompt:
    inline: |
      You receive the original query, a research plan, and raw findings.
      Produce a short report with an Executive Summary (2 sentences),
      Key Findings (3-5 bullets, each citing a URL), and Conclusion (1-2 sentences).
  handler:
    typeName: declarative
  protocols:
    - kind: Http
  llmGatewayRef: demo-llm-gateway
  tools: []
  budget:
    maxTurns: 1
```

Apply.

```bash
vais apply -f planner.yaml
vais apply -f researcher.yaml
vais apply -f reporter.yaml

vais get agents
# NAME         VERSION   STATUS   AGE
# planner      1.0       Active   …
# researcher   1.0       Active   …
# reporter     1.0       Active   …
```

Smoke-test each agent individually before wiring them up.

```bash
vais invoke planner --text "Why is the sky blue?"
vais invoke researcher --text "Fetch https://example.com and summarise."
vais invoke reporter --text "Compose a report from these findings: ..."
```

---

## 5. Compose them into a graph and invoke (3 min)

**`research-pipeline.yaml`** — three sequential nodes with shared state.

```yaml
apiVersion: vais.agents/v1
kind: AgentGraph
metadata:
  id: research-pipeline
  version: "1.0"
  description: planner → researcher → reporter.
spec:
  entry: plan
  maxSteps: 10
  nodes:
    - id: plan
      kind: Agent
      ref: { id: planner, version: "1.0" }
      stateBindings:
        input:  [query]
        output: [research_plan]
    - id: research
      kind: Agent
      ref: { id: researcher, version: "1.0" }
      stateBindings:
        input:  [query, research_plan]
        output: [research_findings]
    - id: report
      kind: Agent
      ref: { id: reporter, version: "1.0" }
      stateBindings:
        input:  [query, research_plan, research_findings]
        output: [final_report]
    - id: end
      kind: End
  edges:
    - from: plan
      to: research
    - from: research
      to: report
    - from: report
      to: end
```

```bash
vais apply -f research-pipeline.yaml
vais get-graphs
# NAME                 VERSION   STATUS   AGE
# research-pipeline    1.0       Active   …
```

Invoke it. The streaming form prints every graph and node event.

```bash
vais invoke-graph research-pipeline \
  --initial-state '{"query": "What is Model Context Protocol and why does it matter?"}' \
  --stream
```

<details><summary>PowerShell</summary>

```powershell
vais invoke-graph research-pipeline `
  --initial-state '{"query": "What is Model Context Protocol and why does it matter?"}' `
  --stream
```

</details>

Expected event stream.

```
graph.started     research-pipeline
node.started      plan
node.completed    plan
edge.traversed    plan → research
node.started      research
tool.called       fetch
tool.completed    fetch
node.completed    research
edge.traversed    research → report
node.started      report
node.completed    report
graph.completed   research-pipeline
```

The final report lands in `state.final_report` on `graph.completed`.

> **What the gateways did.** Every `node.completed` from `plan`, `research`, and `report` triggered an LLM call routed through `demo-llm-gateway` — you'll see `LlmLogging` entries in `docker logs vais-runtime`. Every `tool.called` from `research` went through `demo-mcp-governance` — the rate limiter and the 8 192-char truncation applied transparently.

---

## 6. Optional bonus — swap one node for a Python agent (~5 min)

Container plugins are the recommended Phase-2 way to ship polyglot agents. A plugin is its own Docker image that speaks the IP-1 HTTP protocol (`/health`, `/v1/metadata`, `/v1/invoke`, `/v1/stream`). The runtime supervises the plugin container — pulls, starts, drains, and replaces it — via the Docker socket mounted in step 1. **No runtime image rebuild. No silo restart.**

A ready-to-use Python plugin lives at [`samples/quickstart-python-planner/`](samples/quickstart-python-planner/):

```
samples/quickstart-python-planner/
├── plugin.yaml              # runtime descriptor with spec.build block
├── Dockerfile               # builds the plugin's HTTP server image
├── pyproject.toml           # minimal Python deps
└── server.py                # stdlib HTTP server implementing the IP-1 protocol
```

### 6a. Register the plugin with the runtime

`vais apply -f` reads `spec.build`, builds the image locally (skipped if the tag already exists), and registers the plugin with the running runtime in one shot. No separate `docker build` step needed.

```bash
vais apply -f <repo>/agentic/samples/quickstart-python-planner/plugin.yaml
# vais-quickstart-planner:1.0 built ✓
# quickstart-python-planner created (container-plugin, version 1.0)
```

<details><summary>PowerShell</summary>

```powershell
vais apply -f "<repo>/agentic/samples/quickstart-python-planner/plugin.yaml"
```

</details>

```bash
vais plugin-status
# NAME                       KIND        TOPOLOGY    STATE   IMAGE
# quickstart-python-planner  Container   standalone  Ready   vais-quickstart-planner:1.0
```

### 6b. Repoint the `planner` agent at the Python handler

Edit `planner.yaml` — change `handler.typeName` to match the plugin's `/v1/metadata` (`quickstart_planner.QuickstartPlanner`) and drop the now-irrelevant `model` and `systemPrompt` blocks. The Python plugin owns its own prompt and LLM client.

```diff
 spec:
-  model:
-    provider: openai
-    name: gpt-4o-mini
-  systemPrompt:
-    inline: |
-      You decompose ...
   handler:
-    typeName: declarative
+    typeName: quickstart_planner.QuickstartPlanner
   protocols:
     - kind: Http
-  llmGatewayRef: demo-llm-gateway
   tools: []
-  budget:
-    maxTurns: 1
```

Re-apply.

```bash
vais apply -f planner.yaml
```

Re-invoke the graph. The runtime routes `node.started plan` to the plugin container over HTTP.

```bash
vais invoke-graph research-pipeline \
  --initial-state '{"query": "What is Model Context Protocol and why does it matter?"}' \
  --stream
```

You'll see the same event sequence — but the `plan` node now executes inside the plugin container.

### 6c. Iterate with `vais apply`

Edit `samples/quickstart-python-planner/server.py` — change the system prompt to ask for **five** sub-questions instead of three. Bump `spec.image` in `plugin.yaml` to `:1.1`, then re-apply:

```bash
# Edit plugin.yaml: spec.image: vais-quickstart-planner:1.1
vais apply -f <repo>/agentic/samples/quickstart-python-planner/plugin.yaml
# → builds :1.1 (new tag), PATCHes the runtime, drain-replace happens
```

The .NET silo never restarts; Orleans grain state is the durability anchor (`opaqueState` round-trips on every `/v1/invoke`, so any plugin replica can serve any call).

For CI pipelines that build and push separately:

```bash
vais plugin-build --image vais-quickstart-planner:1.1 --push
vais apply -f <repo>/agentic/samples/quickstart-python-planner/plugin.yaml --no-build
```

For Kubernetes deployments, `vais plugin-deploy` uses the embedded Helm chart.

> Background and design rationale: [`research/plugin-container-model-2026-05-08.md`](../research/plugin-container-model-2026-05-08.md). Production deployment guide: [`docs/guides/package-a-python-agent.md`](docs/guides/package-a-python-agent.md).

---

## Observability quick look

Tail the runtime logs to see gateway middleware in action.

```bash
docker logs -f vais-runtime
```

Look for:

- `LlmLogging` — one structured entry per LLM call with provider, model, prompt tokens, completion tokens.
- `vais.llm.request` spans (OTel) — wrapping each provider invocation.
- `ToolOtel` spans wrapping each `fetch` tool call.
- `ToolRateLimit` log entries if you exceed 30 fetches per minute (try invoking the graph in a loop).

To export OTel spans to a collector, set `VAIS_OTEL_ENDPOINT=<otlp-url>` on the runtime container; to print to stdout, set `VAIS_OTEL_CONSOLE=true`. See [`docs/concepts/observability.md`](docs/concepts/observability.md) for the full collector setup.

---

## Clean up

```bash
vais delete-graph research-pipeline
vais delete planner researcher reporter
vais delete mcp-servers/mcp-fetch
vais delete mcp-gateways/demo-mcp-governance
vais delete llm-gateways/demo-llm-gateway

docker compose down
docker rm -f vais-plugin-quickstart-python-planner 2>/dev/null || true
```

---

## Where to go next

| You want to … | Read |
|---|---|
| Understand the runtime topology (localhost vs clustered, Orleans grains) | [`docs/concepts/control-plane.md`](docs/concepts/control-plane.md) |
| Write your own LLM gateway middleware | [`docs/guides/plug-in-gateway-middleware.md`](docs/guides/plug-in-gateway-middleware.md) |
| Write your own tool gateway middleware | [`docs/guides/gate-tool-calls-with-the-tool-gateway.md`](docs/guides/gate-tool-calls-with-the-tool-gateway.md) |
| Add conditional routing (`if/else`) to the graph | [`docs/concepts/graph-orchestration.md`](docs/concepts/graph-orchestration.md) |
| Run a Python agent in production (Kubernetes, secrets) | [`docs/guides/package-a-python-agent.md`](docs/guides/package-a-python-agent.md) |
| Make graphs resumable across silo restarts | [`docs/guides/run-resumable-graphs-on-orleans.md`](docs/guides/run-resumable-graphs-on-orleans.md) |
| Browse every package, sample, and reference | [`docs/index.md`](docs/index.md) |
