# Connect OpenWebUI to the runtime

You'll verify the runtime's OpenAI-compatible endpoint, start OpenWebUI, point it at the runtime, and chat with both an agent and a graph — all without any code. About 15 minutes. End state: OpenWebUI shows `agent:greeter` and optionally `graph:qa-pipeline` as selectable models in its dropdown, and you can hold a multi-turn conversation with each.

## How the endpoint works

The runtime exposes two standard OpenAI-compatible routes:

| Route | Purpose |
|---|---|
| `GET /v1/models` | Lists all available models: LLM aliases + every registered agent (prefixed `agent:`) + annotated graphs (prefixed `graph:`). |
| `POST /v1/chat/completions` | Routes to the LLM gateway, an agent, or a graph based on the `model` field. Supports `"stream": true`. |

**Model naming convention:**

- `agent:<id>` — routes to the agent registered under that id.
- `graph:<id>` — routes to the graph registered under that id. The graph must carry a `vais.io/openai-compat-input-key` annotation (see [Part 3](#part-3--expose-a-graph-as-a-model)).

Multi-turn context for agents works through the standard OpenAI `messages` array: OpenWebUI sends the full conversation history with each request, and the runtime replays it as `InitialHistory` before the new user turn.

## Prerequisites

- A running runtime reachable at `http://localhost:8080`. See **[Deploy the runtime on Docker](../devops/index.md)** if you don't have one.
- The `greeter` agent from **[Your first declarative agent](your-first-declarative-agent.md)** registered. Recreate it if needed:

  ```bash
  vais apply -f greeter.yaml
  ```

- Docker available locally (to run OpenWebUI).

## Part 1 — Verify the endpoint

Confirm the runtime is serving the OpenAI-compat routes before connecting any client.

### Check the model list

```bash
curl http://localhost:8080/v1/models
```

Expected response (truncated):

```json
{
  "object": "list",
  "data": [
    { "id": "agent:greeter", "object": "model", "owned_by": "vais-agent" }
  ]
}
```

If the `greeter` agent is registered, `agent:greeter` appears in the list.

### Send a test completion

```bash
curl http://localhost:8080/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "agent:greeter",
    "messages": [{"role": "user", "content": "Hello!"}]
  }'
```

Expected (abbreviated):

```json
{
  "id": "chatcmpl-...",
  "model": "agent:greeter",
  "choices": [{"message": {"role": "assistant", "content": "Hello! ..."},"finish_reason": "stop"}]
}
```

## Part 2 — Start OpenWebUI

Run the official OpenWebUI Docker image. The `OPENAI_API_BASE_URL` env var tells it where to find the OpenAI-compatible endpoint.

**If the runtime is running directly on your host machine (not in Docker):**

```bash
docker run -d \
  --name openwebui \
  -p 3000:8080 \
  -e OPENAI_API_BASE_URL=http://host.docker.internal:8080/v1 \
  -e OPENAI_API_KEY=not-used \
  ghcr.io/open-webui/open-webui:main
```

`host.docker.internal` resolves to your host from inside a Docker container on Windows and macOS. On Linux, replace it with the Docker bridge IP — typically `172.17.0.1` — or use `--network host` and `http://localhost:8080/v1`.

**If the runtime is running inside Docker on the same `docker-compose` network:**

Add OpenWebUI to your compose file and set `OPENAI_API_BASE_URL` to the runtime's service name:

```yaml
services:
  openwebui:
    image: ghcr.io/open-webui/open-webui:main
    ports:
      - "3000:8080"
    environment:
      OPENAI_API_BASE_URL: http://vais-agents-runtime:8080/v1
      OPENAI_API_KEY: not-used
```

Then open `http://localhost:3000` in your browser.

## Part 3 — Chat with an agent

1. Open `http://localhost:3000`.
2. Create an account on first launch (local only — credentials stay in the OpenWebUI container).
3. Open the model selector and choose `agent:greeter`.
4. Type a message and send. The response comes from the `greeter` agent on the runtime.
5. Continue the conversation — OpenWebUI sends the full message history with each request, giving the agent multi-turn context automatically.

## Part 4 — Expose a graph as a model

Graphs only appear in `GET /v1/models` and accept requests from `POST /v1/chat/completions` when they carry the `vais.io/openai-compat-input-key` annotation. This annotation tells the runtime which state-bag key to write the incoming messages array into.

Update your graph manifest to add the annotation. Using the `qa-pipeline` graph from **[Compose a multi-agent graph](compose-a-multi-agent-graph.md)**:

```yaml
apiVersion: vais.agents/v1
kind: AgentGraph
metadata:
  id: qa-pipeline
  version: "1.0"
  description: Classify intent, then generate the final answer.
  annotations:
    vais.io/openai-compat-input-key: input
    vais.io/openai-compat-output-key: lastAssistantText
spec:
  entry: classify
  nodes:
    - id: classify
      kind: Agent
      ref:
        id: classifier
        version: "1.0"
    - id: respond
      kind: Agent
      ref:
        id: responder
        version: "1.0"
    - id: end
      kind: End
  edges:
    - from: classify
      to: respond
    - from: respond
      to: end
```

**Annotation fields:**

| Annotation | Required | Description |
|---|---|---|
| `vais.io/openai-compat-input-key` | Yes | State-bag key the runtime writes the `messages` array into when the graph is invoked via `/v1/chat/completions`. |
| `vais.io/openai-compat-output-key` | No | State-bag key the runtime reads to produce the assistant reply. Defaults to the same value as `input-key` when omitted. |

Apply the updated manifest:

```bash
vais apply -f qa-pipeline.yaml
```

`graph:qa-pipeline` now appears in the model list. Select it in OpenWebUI and send a message — the graph runs end-to-end and the last assistant turn is returned as the reply.

## Routing flags

By default both `agent:*` and `graph:*` models are enabled. Disable either via env var on the runtime container:

| Env var | Default | Effect |
|---|---|---|
| `Vais__OpenAiCompat__AgentRoutingEnabled` | `true` | When `false`, `agent:*` models are hidden from `/v1/models` and return 404 on invocation. |
| `Vais__OpenAiCompat__GraphRoutingEnabled` | `true` | When `false`, `graph:*` models are hidden from `/v1/models` and return 404 on invocation. |

## Group a conversation's calls into one run

Send an optional `X-Run-Id` header to correlate a sequence of requests as one run
in telemetry. On `agent:`/`graph:` models it doubles as the session / run id, so
repeated calls with the same value share session continuity; on the plain LLM path
it groups the completions under one run in Langfuse. Without it, the run id is
identity-derived or minted per call; an explicit header overrides the
identity-derived value.

```bash
curl http://localhost:8080/v1/chat/completions \
  -H "Content-Type: application/json" \
  -H "X-Run-Id: chat-session-7" \
  -d '{
    "model": "agent:greeter",
    "messages": [{"role": "user", "content": "Hello again!"}]
  }'
```

OpenWebUI does not send this header itself; it is for clients (such as a coding
agent) that make many calls per session and want them grouped.

## What you built

- A direct connection from OpenWebUI to the runtime's OpenAI-compatible endpoint.
- Multi-turn chat with a declarative agent using the standard `messages` history convention.
- A graph exposed as a selectable model via the `vais.io/openai-compat-input-key` annotation.

## Next

- **[Evaluate an agent](evaluate-an-agent.md)** — author a regression suite, score responses with built-in assertions, and gate CI with JUnit XML output.
- **[Deep agent development](../deep-development/index.md)** — author plugin code when declarative YAML isn't enough.
- **[Wire the LLM gateway](wire-the-llm-gateway.md)** — add logging, OTel, and rate limiting to every model call.
- [Reference → Runtime configuration](../reference/runtime-configuration.md) — full env-var reference including OpenAI-compat routing flags.
