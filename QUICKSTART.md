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

> **Status.** Pre-alpha. The runtime and CLI are built from source out of this repo — neither is published to a public registry yet. APIs may move between commits.

---

## Prerequisites

| Tool | Version | Why |
|---|---|---|
| Docker Engine / Docker Desktop | 24+ / 4.30+ | Runtime + MCP fetch sidecar |
| .NET 10 SDK | 10.0+ | `vais` CLI is a global `dotnet tool` |
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

## 1. Start the runtime (2 min)

Build the runtime image (first time ~2 min; cached ~20 s).

```bash
docker build -f src/Vais.Agents.Runtime.Host/Dockerfile -t vais-agents-runtime:local .
```

> **Where does the MCP fetch server come from?** The runtime supervises it in step 3 — you don't need to build or run it yourself. `samples/mcp-fetch-container/` ships a thin Dockerfile + bridge that wraps the official `mcp-server-fetch` PyPI package (which only speaks stdio) behind an HTTP MCP bridge; the runtime builds the image on `vais apply` and manages the container lifecycle. See [§3 below](#3-apply-the-gateway-configs-and-register-the-mcp-server-2-min).

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

Save this as `docker-compose.yaml` in `~/quickstart/` — runtime only, with the wiring needed so the runtime can supervise both step-3 container MCP servers and step-6 container plugins.

```yaml
networks:
  vais:
    name: vais-quickstart            # named so the runtime can spawn plugin containers onto the same network

services:
  runtime:
    image: vais-agents-runtime:local
    container_name: vais-runtime
    user: root                                                # required so the runtime can read /var/run/docker.sock (see below)
    environment:
      VAIS_HOSTING_MODE: localhost
      ASPNETCORE_URLS: http://0.0.0.0:8080
      OPENAI_API_KEY: ${OPENAI_API_KEY}
      VAIS_CONTAINER_PLUGINS_DIRECTORY: /var/lib/vais/plugins # enables container plugins (step 6) AND container MCP servers (step 3)
      VAIS_DOCKER_PLUGIN_NETWORK: vais-quickstart             # supervised containers join this network; runtime reaches them by container DNS
      Vais__ContainerPlugin__CallTokenSecret: ${VAIS_CALL_TOKEN_SECRET}  # required when container plugins are enabled (≥32 chars)
    ports:
      - "8080:8080"
    networks:
      - vais
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock             # lets the runtime spawn supervised containers
    healthcheck:
      test: ["CMD", "wget", "-qO-", "http://localhost:8080/healthz"]
      interval: 10s
      timeout: 3s
      retries: 5
      start_period: 15s
```

Set a call-token secret (any ≥32-char string — used only inside the local stack):

```bash
export VAIS_CALL_TOKEN_SECRET=$(openssl rand -hex 32)
```

<details><summary>PowerShell</summary>

```powershell
$env:VAIS_CALL_TOKEN_SECRET = -join ((1..64) | ForEach-Object { '{0:x}' -f (Get-Random -Maximum 16) })
```

</details>

> **Why mount `docker.sock` and run as `root`?** The runtime supervises step-3 container MCP servers and step-6 container plugins via the Docker API. The runtime image's default non-root user (uid 65532) cannot read the socket (it is owned `root:root 0660` on Docker Desktop and most Linux hosts), so we override `user: root` for the local stack. In production you would instead `group_add` the host's docker GID. If you have no intention of using container MCP servers or container plugins, drop the `docker.sock` mount, `user: root`, `VAIS_CONTAINER_PLUGINS_DIRECTORY`, `VAIS_DOCKER_PLUGIN_NETWORK`, and `Vais__ContainerPlugin__CallTokenSecret`.

> **Why the explicit `vais-quickstart` network?** When `VAIS_DOCKER_PLUGIN_NETWORK` is set, the supervisor attaches every supervised container to that network and the runtime reaches them via Docker's embedded DNS (`http://<container-name>:<port>`). Without this, `localhost:<port>` from inside the runtime container can't reach supervised containers.

Start the runtime.

```bash
docker compose up -d
```

Verify it is healthy.

```bash
curl -s http://localhost:8080/healthz   # → Healthy
```

<details><summary>PowerShell</summary>

```powershell
curl.exe -s http://localhost:8080/healthz
```

The PowerShell `curl` alias points at `Invoke-WebRequest`; `curl.exe` is the real client and ships with Windows 10+.

</details>

---

## 2. Install and point the CLI (1 min)

The CLI is not yet published to nuget.org. Build it from source out of this repo (you are already in `<repo>/agentic`):

```bash
dotnet pack src/Vais.Agents.Cli/Vais.Agents.Cli.csproj -c Release -o ./nupkgs
dotnet tool install -g --add-source ./nupkgs Vais.Agents.Cli
vais version                                            # → vais v0.0.1
```

Point the CLI at the running runtime and list agents.

```bash
vais config set-context local --server http://localhost:8080
vais config use-context local
vais get                                                # → (no agents)
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

**`mcp-fetch-server.yaml`** — point at the reference container MCP server. `transport: containerStdio` tells the runtime to supervise the container itself; `spec.container.build` triggers a local `docker build` on `vais apply`.

```yaml
apiVersion: vais.agents/v1
kind: McpServer
metadata:
  id: mcp-fetch
  version: "1.0"
  description: HTTP fetch tool via MCP — runtime-supervised container.
spec:
  transport: containerStdio
  mcpGatewayRef: demo-mcp-governance
  container:
    build:
      context: <repo>/agentic/samples/mcp-fetch-container   # absolute path; CLI builds the image
    env:
      MCP_STDIO_CMD: "python -m mcp_server_fetch"
```

> **What this does.** The CLI builds the image (tag derived: `vais-mcp-mcp-fetch:1.0`), POSTs the manifest. The runtime's container supervisor starts a hardened container attached to the `vais-quickstart` network; an in-container bridge wraps the stdio `mcp-server-fetch` child and exposes it over streamableHttp at `/mcp`. The runtime opens an MCP client to it within ~30 s of apply. See [`samples/mcp-fetch-container/`](samples/mcp-fetch-container/) for the bridge details and [`samples/mcp-stdio-template/`](samples/mcp-stdio-template/) for the generic pattern.

Apply in dependency order.

```bash
vais apply -f llm-gateway.yaml
vais apply -f mcp-gateway.yaml
vais apply -f mcp-fetch-server.yaml      # builds the image (~30s first time), registers the server
```

Each command prints `<id> created (<kind>, version <version>)` (or `updated` on subsequent applies).

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
    id: gpt-4o-mini
    apiKeyRef: secret://env/OPENAI_API_KEY
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
    id: gpt-4o-mini
    apiKeyRef: secret://env/OPENAI_API_KEY
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
    id: gpt-4o-mini
    apiKeyRef: secret://env/OPENAI_API_KEY
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

vais get
# ID           VERSION   DESCRIPTION                                              LABELS
# planner      1.0       Decomposes a query into 3 focused sub-questions.         -
# researcher   1.0       Fetches web content for each sub-question and …          -
# reporter     1.0       Composes the final report from the assembled findings.   -
```

> **Why `apiKeyRef: secret://env/OPENAI_API_KEY`?** The runtime resolves secrets via `secret://<scheme>/<path>` URIs. The built-in `env` resolver reads from environment variables — your `$OPENAI_API_KEY` (forwarded into the runtime container by the compose file) is what each agent's OpenAI provider picks up. File-backed secrets (`secret://file/...`) and KeyVault-style backends are pluggable.

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
# ID                  VERSION   ENTRY   NODES   DESCRIPTION                       LABELS
# research-pipeline   1.0       plan    4       planner → researcher → reporter.  -
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

> **First run only — give the mcp-fetch container ~30 s to start.** After `vais apply -f mcp-fetch-server.yaml`, the container supervisor needs up to ~30 s to spin up the bridge container and complete the MCP handshake. If `node.started research` fires before then, the run fails with `McpServerUnavailable`; just re-invoke. (Tightening this loop is a Phase-1.5 follow-up.)

Expected event stream (timestamps and run IDs vary; prefix glyphs flag event type).

```
? graph.started   16:01:09.148 research-pipeline v1.0 run=10c7cbdd…
? node.started    16:01:09.192 step=0 plan (plan)
~ state.updated   16:01:10.907 step=0 [research_plan, …]
  edge.traversed  16:01:10.915 step=0 plan → research
? node.completed  16:01:10.919 step=0 plan
? node.started    16:01:10.922 step=1 research (research)
  tool.called     16:01:12.501 fetch
  tool.completed  16:01:13.882 fetch
? node.completed  16:01:14.001 step=1 research
  edge.traversed  16:01:14.010 step=1 research → report
? node.started    16:01:14.020 step=2 report (report)
? node.completed  16:01:15.500 step=2 report
? graph.completed 16:01:15.510 research-pipeline
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
# NAME                       KIND       IMAGE                          TOPOLOGY    STATE   API VER   HANDLER/TOOLS
# quickstart-python-planner  container  vais-quickstart-planner:1.0    standalone  ready   0.24      __main__.QuickstartPlanner
```

> **Port choice.** `samples/quickstart-python-planner/plugin.yaml` sets `spec.port: 8090` rather than the default 8080. The `DockerContainerSupervisor` binds the plugin's port to `127.0.0.1:<port>` on the host (P12 hardening); since the runtime is already publishing host `:8080`, the plugin must pick a different port. The SDK reads `VAIS_PLUGIN_PORT` (injected by the supervisor), so no Python code change is needed.

### 6b. Repoint the `planner` agent at the Python handler

Edit `planner.yaml` — change `handler.typeName` to match the plugin's `/v1/metadata` (`__main__.QuickstartPlanner` — `__main__` because the toy server is invoked directly as `python server.py`) and drop the now-irrelevant `model`, `systemPrompt`, `llmGatewayRef`, and `budget` blocks. The Python plugin owns its own prompt and LLM client.

```diff
 spec:
-  model:
-    provider: openai
-    id: gpt-4o-mini
-    apiKeyRef: secret://env/OPENAI_API_KEY
-  systemPrompt:
-    inline: |
-      You decompose ...
   handler:
-    typeName: declarative
+    typeName: __main__.QuickstartPlanner
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

## OpenAI-compatible gateway

`Vais.Agents.Gateways.OpenAiCompat` is already wired into the runtime image —
`POST /v1/chat/completions` and `GET /v1/models` are mounted on the same `:8080`
port the rest of the runtime serves. **No `Program.cs` changes, no extra
registration step.** Any client that speaks the OpenAI Chat Completions API
(OpenWebUI, LiteLLM, Continue.dev, …) can point at the runtime and call your
agents and graphs directly.

`GET /v1/models` automatically discovers:

- Every registered agent — exposed as `agent:<id>`.
- Every graph that opts in via the `vais.io/openai-compat-input-key` annotation
  — exposed as `graph:<id>`.

(To turn either off, set `Vais__OpenAiCompat__AgentRoutingEnabled=false` or
`Vais__OpenAiCompat__GraphRoutingEnabled=false` on the runtime container.)

### Call an agent

```bash
curl http://localhost:8080/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "agent:planner",
    "messages": [
      {"role": "user", "content": "What are the main themes of MCP?"}
    ]
  }'
```

Multi-turn is stateless: send the full `messages` array on each request and the
gateway re-seeds history from messages before the last `user` entry. That matches
how OpenWebUI, LiteLLM, and most chat UIs already behave — no session header
required.

### Call a graph

A graph opts in with two annotations under `metadata.annotations`. Edit
`research-pipeline.yaml` from §5 to add them:

```yaml
apiVersion: vais.agents/v1
kind: AgentGraph
metadata:
  id: research-pipeline
  version: "1.0"
  annotations:
    vais.io/openai-compat-input-key: query           # state field to write the user message into
    vais.io/openai-compat-output-key: final_report   # state field to read the assistant reply from
spec:
  entry: plan
  # ... nodes, edges, as before
```

Re-apply, then call it like any other model:

```bash
vais apply -f research-pipeline.yaml

curl http://localhost:8080/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "graph:research-pipeline",
    "messages": [
      {"role": "user", "content": "Summarise the Model Context Protocol spec"}
    ]
  }'
```

The gateway writes the last user message into the `query` field of the initial
state and reads `final_report` from the final state as the assistant reply. If
the output key holds a list of chat messages, the last assistant message is
returned. A graph that lacks the input annotation is hidden from `/v1/models` and
returns `422 Unprocessable Entity` if invoked directly.

### Caller parameters

`temperature`, `max_tokens`, `tools`, and `tool_choice` from the request body are
forwarded to the agent as metadata keys (`oai.temperature`, `oai.max_tokens`,
`oai.tools`, `oai.tool_choice`) and exposed via `AgentInvocationRequest.Metadata`.
The gateway does not enforce them — honouring them is up to each agent
implementation.

### Group a session's calls into one run

Pass an `X-Run-Id` header to tie a sequence of calls to one run in telemetry.
On the LLM path it stamps the run/correlation id so a multi-turn client's
completions roll up under one run in Langfuse instead of appearing as N unrelated
calls; on `agent:`/`graph:` models it is used as the session / run id, so repeated
calls with the same value share session continuity.

```bash
curl http://localhost:8080/v1/chat/completions \
  -H "Content-Type: application/json" \
  -H "X-Run-Id: my-session-42" \
  -d '{
    "model": "agent:planner",
    "messages": [{"role": "user", "content": "continue where we left off"}]
  }'
```

When the header is absent the run id is identity-derived (from your
`IInboundIdentityResolver`) or minted per call. An explicit `X-Run-Id` overrides
an identity-derived id, so a caller can scope correlation per session even on a
shared API key.

### Connect OpenWebUI

1. Settings → Connections → Add OpenAI connection.
2. **Base URL** — `http://<runtime-host>:8080/v1`.
3. **API Key** — any non-empty string (the gateway accepts any value; auth is
   handled by upstream JWT middleware if you configure it).
4. Your agents and graphs appear in the model selector.

OpenWebUI sends the full message history on every request, so stateless
multi-turn works out of the box.

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
vais delete planner --force
vais delete researcher --force
vais delete reporter --force
vais delete mcp-servers/mcp-fetch --force
vais delete mcp-gateways/demo-mcp-governance --force
vais delete llm-gateways/demo-llm-gateway --force

docker compose down
docker rm -f vais-plugin-mcp-fetch vais-plugin-quickstart-python-planner 2>/dev/null || true
docker rmi vais-quickstart-planner:1.0 vais-mcp-mcp-fetch:1.0 vais-agents-runtime:local 2>/dev/null || true
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
| Expose agents to OpenWebUI / LiteLLM via the OpenAI-compatible gateway | [OpenAI-compatible gateway](#openai-compatible-gateway) (this file) |
| Browse every package, sample, and reference | [`docs/index.md`](docs/index.md) |
