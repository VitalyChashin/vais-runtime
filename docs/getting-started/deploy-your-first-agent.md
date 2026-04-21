# Deploy your first agent

This walkthrough takes you from zero to a running declarative agent in 10 minutes. You write a YAML manifest, push it to the runtime, and invoke it. No C# required.

## Prerequisites

- A running runtime reachable at `http://localhost:8080`. See [Install the runtime](install-the-runtime.md) if you haven't started one.
- `vais` CLI installed and a context pointing at it:

  ```bash
  vais config set-context local --server http://localhost:8080
  vais config use-context local
  ```

- An OpenAI API key in the environment for the model call:

  ```bash
  export OPENAI_API_KEY=sk-...
  ```

  The runtime reads this from the environment. Pass it via `docker compose` or Helm values in production.

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
    name: gpt-4o-mini
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

- `model.provider: openai` — uses the built-in OpenAI model provider factory.
- `model.name: gpt-4o-mini` — forwarded to the OpenAI API as the model name.
- `systemPrompt.inline` — embedded system prompt (no file or template ref needed).
- `handler.typeName: declarative` — the manifest instantiation tier wires `StatefulAiAgent` from the `model` + `systemPrompt` fields; no plugin DLL required.

## Step 2 — Apply the manifest

```bash
vais apply -f greeter.yaml
```

Expected output:

```
applied Agent greeter@1.0
```

Under the hood `vais apply` sends `POST /v1/agents` with the manifest envelope. The runtime stores it in its Orleans-backed registry (`OrleansAgentRegistry`).

## Step 3 — Confirm registration

```bash
vais get
```

```
NAME       VERSION   STATUS   AGE
greeter    1.0       Active   3s
```

```bash
vais get greeter
```

Prints the full manifest back as YAML.

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

The runtime updates the stored manifest. The next invoke picks up the new system prompt without restarting the container.

## Step 7 — Clean up

```bash
vais delete greeter
```

This evicts the agent from the runtime's registry and cancels any in-flight work.

## Next

- [From zero to graph in 20 minutes](../tutorials/from-zero-to-graph-in-20-minutes.md) — compose multiple agents into a graph.
- [Author an agent in YAML](../guides/author-an-agent-in-yaml.md) — full YAML field reference.
- [Declarative agents concept](../concepts/declarative-agents.md) — how the manifest instantiation tier works.
- [Package an agent as a plugin](../guides/package-an-agent-as-a-plugin.md) — bring your own C# agent code to the runtime.
