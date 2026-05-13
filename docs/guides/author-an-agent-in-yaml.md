# Guide: author an agent in YAML

End-to-end walkthrough from a blank YAML file to a running agent that answers a prompt. No C# code required.

Prereqs: a running `vais-agents-runtime` container ([install-the-runtime-locally](install-the-runtime-locally.md)), the `vais` CLI ([install-the-cli](../getting-started/install-the-cli.md)), and an OpenAI / Anthropic / Azure-OpenAI API key.

## 1. Export your API key

The runtime reads API keys via the `ISecretResolver` chain. The default chain handles `secret://env/NAME` and `secret://file/path`. We'll use the env form:

```bash
export OPENAI_API_KEY=sk-...
docker compose -f deploy/compose/docker-compose.localhost.yml up -d
```

The runtime picks up `OPENAI_API_KEY` from its own env because the `docker-compose.localhost.yml` base forwards the host environment. Verify:

```bash
curl -s http://localhost:8080/healthz
# {"status":"Healthy"}
```

## 2. Write the manifest

Save as `weather.yaml`:

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: weather
  version: "1.0"
  labels:
    tier: experimentation
spec:
  description: A weather-curious assistant.
  handler:
    typeName: declarative
  protocols:
    - kind: Http
  tools: []
  model:
    provider: openai
    id: gpt-4o-mini
    apiKeyRef: secret://env/OPENAI_API_KEY
    temperature: 0.3
  systemPrompt:
    inline: |
      You help users understand weather conditions.
      Keep answers to one paragraph.
      If the user asks about something other than weather, politely redirect.
  budget:
    maxTurns: 3
    maxDuration: PT15S
```

Key fields:
- **`handler.typeName: declarative`** — sentinel for the declarative path. The plugin loader (v0.18+) repurposes specific names for code-authored plugins.
- **`model.provider: openai`** — matches `OpenAIModelProviderFactory.ProviderName`.
- **`model.apiKeyRef`** — `secret://env/OPENAI_API_KEY` tells the resolver to read the `OPENAI_API_KEY` env var.
- **`systemPrompt.inline`** — literal prompt. See [declarative-agents concept](../concepts/declarative-agents.md#systempromptspec) for templateRef / fileRef alternatives.
- **`budget`** — caps the run at 3 turns or 15 seconds, whichever hits first.

## 3. Apply the manifest

```bash
vais apply -f weather.yaml
# ✓ weather:1.0 applied (created)
```

Behind the scenes: `POST /v1/agents` → `AgentLifecycleManager.CreateAsync` → `OrleansAgentRegistry.Register` → grain persists the JSON payload.

Check what the registry holds:

```bash
vais get agents
# NAME     VERSION  HANDLER        MODEL
# weather  1.0      declarative    openai/gpt-4o-mini
```

## 4. Invoke

```bash
vais invoke weather --text "What should I wear for a 10C day with rain?"
# Layer a waterproof shell over a warm mid-layer — wool or fleece — plus long
# trousers. Shoes with good grip help on wet surfaces. Don't forget a hat or
# umbrella; 10 °C feels colder in rain than in dry air.
```

The pipeline: `POST /v1/agents/weather/invoke` → lifecycle-manager + runtime → `OrleansAgentRuntime.GetOrCreate("weather")` → grain activates → `ConfigureAgentGrains` factory → translator loads manifest → builds `StatefulAgentOptions` with an `OpenAI`-backed provider → `StatefulAiAgent.AskAsync` → real OpenAI response.

## 5. Update + re-invoke

Tweak the system prompt:

```yaml
# weather.yaml (edited)
spec:
  systemPrompt:
    inline: |
      You are a grumpy meteorologist.
      Keep answers to one paragraph.
      Complain about anyone who can't read a basic forecast.
```

Apply again:

```bash
vais apply -f weather.yaml
# ✓ weather:1.0 applied (updated)
```

Next invoke picks up the new prompt:

```bash
vais invoke weather --text "Will it rain tomorrow?"
# *sigh* — if you'd bother opening a forecast app, you'd see the cold front
# moving in tomorrow afternoon. Expect rain by 3 PM. Bring an umbrella, but
# hold it right-side up this time. ...
```

v0.17 semantic: update evicts the grain's cached options + invalidates the translator cache. In-flight runs aren't killed; the next invoke sees the new shape.

## 6. Add tools

Tools in v0.17 come from three sources — `static:` (consumer-registered `ITool`s in DI), `mcp:` (MCP servers declared in `McpServers`), and `a2a:` (remote A2A agents declared in `A2ARemoteAgents`).

For a YAML-only example, MCP is easiest: declare a public weather-MCP server and reference it:

```yaml
spec:
  # ... other fields ...
  tools:
    - name: weather-forecast
      source: mcp:open-meteo
  mcpServers:
    - name: open-meteo
      transport: streamableHttp
      url: https://mcp.example.org/weather
```

⚠️ **v0.17 limitation:** the translator validates that `mcp:open-meteo` references a declared `McpServers[].Name`, but does **not** yet materialize the `McpToolSource`. Lazy MCP materialization lands post-v0.17. Same for `a2a:` — validation today, instantiation later.

Until then, tool use comes via `static:*` (register your own `ITool` impls in the host composition). See [ship-a-guardrail](ship-a-guardrail.md) for the registration pattern — guardrails and static tools follow the same DI-by-factory shape.

## 7. Add guardrails

Cap user input length and require weather-related topics:

```yaml
spec:
  # ... other fields ...
  guardrails:
    input:
      - name: LengthCap
        params: { maxChars: 500 }
      - name: RegexAllowlist
        params: { pattern: "(?i)weather|rain|snow|temperature|forecast|umbrella|wind" }
    output:
      - name: RegexDenylist
        params: { pattern: "(?i)password|credit.?card|ssn" }
```

Apply. Now a 600-character rant hits `LengthCap` → invoke returns `urn:vais-agents:guardrail-denied` before reaching the model. A question about cryptocurrency hits the `RegexAllowlist` → same URN.

See [declarative-agents concept §GuardrailsSpec](../concepts/declarative-agents.md#guardrailsspec) for the full factory list and [author-a-rego-policy-against-the-vais-input-schema](author-a-rego-policy-against-the-vais-input-schema.md) for OPA-level gating that runs before guardrails.

## 8. Delete

```bash
vais delete agent weather
# ✓ weather:1.0 deleted
```

Registry row gone + grain evicted + translator cache cleared. Re-applying brings the agent back fresh.

## Troubleshooting

- **`400 urn:vais-agents:model-provider-unsupported`** — `ModelSpec.Provider` doesn't match a registered factory. Check `vais version` to confirm the runtime registered `openai / anthropic / azure-openai` at startup.
- **`400 urn:vais-agents:manifest-invalid`** — syntactic issue. Check the JSON Schema the runtime emits at `GET /openapi/v1.json` — the `AgentManifest` component is authoritative.
- **`501 urn:vais-agents:handler-not-loaded`** — manifest has no `Model` and no loaded plugin claims its `handler.typeName`. Either add a `Model` block (declarative path) or load a plugin that exports the handler.
- **Secret resolution fails** — `secret://env/OPENAI_API_KEY` requires the env var be set *inside* the runtime container. If you're running via docker-compose, export it on the host before `docker compose up`; the compose files forward the env.

## Related

- [declarative-agents concept](../concepts/declarative-agents.md) — the full design story.
- [ship-a-guardrail](ship-a-guardrail.md) — custom `IGuardrailFactory`.
- [ship-a-custom-model-provider](ship-a-custom-model-provider.md) — extend beyond the three built-in providers.
- [manifest-schema reference](../reference/manifest-schema.md) — every field, every type.
- [install-the-runtime-locally](install-the-runtime-locally.md) — stand up the runtime if you haven't yet.
