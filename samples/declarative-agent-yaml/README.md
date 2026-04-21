# declarative-agent-yaml

Deploy a declarative agent to the runtime using a YAML manifest and the `vais` CLI. Zero C# required.

**Concepts:** [declarative agents](../../docs/concepts/declarative-agents.md), [deploy your first agent](../../docs/getting-started/deploy-your-first-agent.md), [author an agent in YAML](../../docs/guides/author-an-agent-in-yaml.md).
**Needs API key:** yes — `OPENAI_API_KEY` for live LLM calls (the agent won't invoke without it).
**Code:** 0 lines — YAML manifests only.

---

## Quickstart

### 1. Start the runtime

```bash
cd oss/agentic
docker build -f src/Vais.Agents.Runtime.Host/Dockerfile -t vais-agents-runtime:local .
docker compose -f deploy/compose/docker-compose.base.localhost.yaml up -d
```

Verify:

```bash
curl http://localhost:8080/healthz   # → ok
```

Set the CLI context:

```bash
vais config set-context local --server http://localhost:8080
vais config use-context local
```

### 2. Deploy the greeter agent

```bash
vais apply -f samples/declarative-agent-yaml/greeter.yaml
# applied Agent greeter@1.0

vais get greeter
# NAME      VERSION  STATUS  AGE
# greeter   1.0      Active  2s
```

### 3. Invoke

```bash
vais invoke greeter --text "Hello!"
# Hello there — great to meet you! How can I help you today?
```

Stream token-by-token:

```bash
vais invoke greeter --text "Tell me about Paris." --stream
```

### 4. Update the system prompt without a restart

Edit `greeter.yaml` — change `systemPrompt.inline` to anything you like, then re-apply:

```bash
vais apply -f samples/declarative-agent-yaml/greeter.yaml
vais invoke greeter --text "Hello again!"
```

The running container picks up the new prompt immediately.

### 5. Deploy the second agent

```bash
vais apply -f samples/declarative-agent-yaml/summarizer.yaml
vais invoke summarizer --text "Artificial intelligence is transforming every industry..."
```

### 6. Clean up

```bash
vais delete greeter
vais delete summarizer
docker compose -f deploy/compose/docker-compose.base.localhost.yaml down
```

---

## Manifests in this sample

### `greeter.yaml`

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

### `summarizer.yaml`

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: summarizer
  version: "1.0"
  description: Condenses long text to three bullet points.
spec:
  model:
    provider: openai
    name: gpt-4o-mini
  systemPrompt:
    inline: |
      You are a summarization assistant. Given any text, respond with exactly three
      bullet points that capture the key ideas. No preamble, no trailing commentary.
  handler:
    typeName: declarative
  protocols:
    - kind: Http
  tools: []
```

---

## Environment variables

| Variable | Purpose |
|---|---|
| `OPENAI_API_KEY` | Required — forwarded to the OpenAI model provider |

---

## See also

- [docs/getting-started/deploy-your-first-agent.md](../../docs/getting-started/deploy-your-first-agent.md)
- [docs/guides/author-an-agent-in-yaml.md](../../docs/guides/author-an-agent-in-yaml.md)
- [docs/concepts/declarative-agents.md](../../docs/concepts/declarative-agents.md)
- [docs/reference/agent-manifest.md](../../docs/reference/agent-manifest.md)
