# Your first declarative agent

You'll write a YAML manifest, push it to the runtime, and invoke it. About 10 minutes, no C# required. End state: a `greeter` agent that responds to `vais invoke greeter --text "Hello!"` with a real model response.

## Prerequisites

- A running runtime reachable at `http://localhost:8080`. See **[Deploy the runtime on Docker](../devops/index.md)** if you don't have one.
- `vais` CLI installed and a context pointing at the runtime:

  ```bash
  vais config set-context local --server http://localhost:8080
  vais config use-context local
  ```

- An OpenAI API key forwarded into the runtime container. `docker-compose.localhost.yml` reads `OPENAI_API_KEY` from your shell automatically — just export it before starting (or restarting) the runtime:

  ```bash
  export OPENAI_API_KEY=sk-...          # bash / zsh
  # $env:OPENAI_API_KEY = "sk-..."      # PowerShell
  ```

  Then start (or restart) the runtime so the value is picked up:

  ```bash
  cd agentic/deploy/compose
  docker compose -f docker-compose.localhost.yml up -d
  ```

  For Kubernetes or other runtimes, pass the key as a secret-backed env var in the Helm values.

## Step 1 — Write the manifest

Save the following as `greeter.yaml`:

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: greeter
  version: "1.0"
  description: Friendly greeting agent.
spec:
  model:
    provider: openai
    id: gpt-4o-mini
    apiKeyRef: secret://env/OPENAI_API_KEY
  systemPrompt:
    inline: |
      You are a friendly assistant that greets the user warmly and asks how you can help.
      Keep your response to 1-2 sentences.
  handler:
    typeName: declarative
  protocols:
    - kind: Http
  tools: []
```

**What these fields mean:**

- `model.provider: openai` — the built-in OpenAI model provider factory.
- `model.id: gpt-4o-mini` — model identifier forwarded to the OpenAI API.
- `model.apiKeyRef: secret://env/OPENAI_API_KEY` — secret URI the runtime resolves to the API key at activation time. `secret://env/` reads from the container's environment.
- `systemPrompt.inline` — embedded system prompt (no file or template ref needed).
- `handler.typeName: declarative` — the manifest instantiation tier wires the agent from the `model` + `systemPrompt` fields; no plugin DLL required.

## Step 2 — Apply the manifest

```bash
vais apply -f greeter.yaml
```

Expected output:

```
greeter created (version 1.0)
```

Under the hood `vais apply` sends `POST /v1/agents` with the manifest envelope. The runtime stores it in its Orleans-backed registry.

## Step 3 — Confirm registration

```bash
vais get
```

```
 ID      | VERSION | DESCRIPTION              | LABELS
---------+---------+--------------------------+--------
 greeter | 1.0     | Friendly greeting agent. | -
```

```bash
vais get greeter
```

Prints the agent record as YAML — the `manifest` stanza (your fields, normalized), plus a `handle` and `status` section added by the runtime.

## Step 4 — Invoke the agent

```bash
vais invoke greeter --text "Hello!"
```

Expected output (real model response):

```
Hello! Welcome — it's great to meet you. How can I help you today?
```

The CLI sends `POST /v1/agents/greeter/invoke` with `{"text": "Hello!"}` and prints the `text` field from the response body.

## Step 5 — Stream the response

For agents that produce longer output, stream token-by-token with `--stream`:

```bash
vais invoke greeter --text "Tell me about Paris." --stream
```

Events fire as the model generates tokens. Press Ctrl-C to stop early.

## Step 6 — Update the manifest

Change the system prompt and re-apply:

```yaml
  systemPrompt:
    inline: "You are a concise assistant. Answer in one sentence only."
```

```bash
vais apply -f greeter.yaml
```

The runtime updates the stored manifest. The new system prompt takes effect once the agent grain deactivates — typically within a few minutes of the last invocation. `vais apply` always prints `created` for localhost mode (the in-memory registry uses upsert), which is expected behavior.

## Step 7 — Clean up

```bash
vais delete greeter
```

This evicts the agent from the runtime's registry and cancels any in-flight work.

## What you built

- A persistent, durable agent definition stored in the runtime's Orleans registry.
- Invocation via HTTP, with optional SSE streaming.
- A live update path — the manifest is the source of truth; replacing it propagates without restart.

## Next

- **[Wire the LLM gateway](wire-the-llm-gateway.md)** — route every model call through observable middleware.
- **[Wire the MCP gateway](wire-the-mcp-gateway.md)** — give the agent tools through the same kind of middleware chain.
- [Author an agent in YAML](../guides/author-an-agent-in-yaml.md) — full manifest field reference.
- [Concepts → Declarative agents](../concepts/declarative-agents.md) — how the manifest instantiation tier works.
