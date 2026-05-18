# Configure LLM providers

You'll wire OpenAI / Anthropic / Azure OpenAI / custom-endpoint credentials into the runtime using the `secret://` indirection — from a `.env` file in local development, from Kubernetes Secrets in production. You'll fan an agent out to multiple providers, point one at a self-hosted endpoint (vLLM / Ollama / LiteLLM / OpenRouter), and chain providers with a Fallback pool. End state: every credential the runtime touches comes from a Secret, not from a checked-in YAML.

## Why every key is a `secret://` URI

The runtime never reads `OPENAI_API_KEY` directly from its environment. Agent manifests carry a **secret reference** — a `secret://<scheme>/<path>` URI — and the runtime resolves it at activation time through an `ISecretResolver` composite. The default composite ships with two schemes:

| URI form                          | Resolves via                                                      | Best for                                              |
|-----------------------------------|-------------------------------------------------------------------|-------------------------------------------------------|
| `secret://env/VAR_NAME`           | `Environment.GetEnvironmentVariable("VAR_NAME")`                  | Local dev, Docker Compose, simple K8s setups          |
| `secret://file/absolute/path`     | `File.ReadAllText(path)`, trailing whitespace trimmed             | Kubernetes projected Secrets, mounted Docker secrets  |

Resolution failures surface as `400 urn:vais-agents:model-provider-unsupported` at activation — the agent never starts with a bad key. Unknown schemes throw at startup with an actionable error.

Why the indirection matters:
- **Manifests stay in git.** The pointer is committed; the secret is not.
- **Keys rotate without manifest changes.** Update the env var or Secret, restart pods, done.
- **Backends are pluggable.** Add a `secret://keyvault/...` resolver in your composition root without touching agent code.

The two built-in schemes cover ~95% of deployments. The "[Custom resolver schemes](#custom-resolver-schemes)" section below shows how to add KeyVault / AWS Secrets Manager / Vault.

## 1. Local development — `secret://env`

The localhost Compose stack forwards the host's `OPENAI_API_KEY` into the runtime container. Copy the template and fill in your keys:

```bash
cd agentic/deploy/compose
cp .env.example .env
# Edit .env — fill OPENAI_API_KEY, optionally ANTHROPIC_API_KEY, etc.

docker compose -f docker-compose.localhost.yml up -d
```

Agent manifest:

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: greeter
  version: "1.0"
spec:
  model:
    provider: openai
    id: gpt-4o-mini
    apiKeyRef: secret://env/OPENAI_API_KEY
  systemPrompt:
    inline: "Be friendly and concise."
  handler:
    typeName: declarative
  protocols:
    - kind: Http
```

`docker-compose.localhost.yml` forwards `OPENAI_API_KEY` out of the box. For other providers, extend the `environment:` block:

```yaml
# docker-compose.override.yml — gitignored
services:
  runtime:
    environment:
      ANTHROPIC_API_KEY: ${ANTHROPIC_API_KEY:-}
      AZURE_OPENAI_API_KEY: ${AZURE_OPENAI_API_KEY:-}
      AZURE_OPENAI_ENDPOINT: ${AZURE_OPENAI_ENDPOINT:-}
      VLLM_API_KEY: ${VLLM_API_KEY:-}
      VLLM_ENDPOINT: ${VLLM_ENDPOINT:-}
```

Run with both files: `docker compose -f docker-compose.localhost.yml -f docker-compose.override.yml up -d`.

## 2. Production — `secret://file` on Kubernetes

In production, prefer `secret://file/...` over `secret://env/...`. File-backed secrets keep keys out of pod environment variables (where they'd otherwise leak to `kubectl describe pod`, sidecars sharing the pod namespace, and crash dumps).

### 2.1 Create the Secret

```bash
kubectl create namespace vais
kubectl create secret generic vais-llm-keys -n vais \
  --from-literal=openai-api-key='sk-...' \
  --from-literal=anthropic-api-key='sk-ant-...'
```

For Azure OpenAI also include the endpoint URL:

```bash
kubectl create secret generic vais-llm-keys -n vais \
  --from-literal=openai-api-key='sk-...' \
  --from-literal=azure-openai-key='...' \
  --from-literal=azure-openai-endpoint='https://my-resource.openai.azure.com/'
```

### 2.2 Project the Secret into the runtime pod

> **Chart limitation (v0.16).** The `vais-agents-runtime` Helm chart does not yet expose `extraVolumes:` / `extraEnv:` knobs. Until it does (see [Known limitations](#known-limitations)), the cleanest production path is to vendor the chart locally and add the projection. The two patterns below give you a working setup today.

**Pattern A — vendor the chart (recommended for production):**

```bash
helm pull oci://your-registry/vais-agents-runtime --version 0.16.0 --untar
# Edit ./vais-agents-runtime/templates/deployment.yaml — add to the container spec:
```

```yaml
volumeMounts:
  - name: llm-keys
    mountPath: /var/run/secrets/vais
    readOnly: true
# ... and at the pod spec level:
volumes:
  - name: llm-keys
    secret:
      secretName: vais-llm-keys
      items:
        - { key: openai-api-key,        path: openai-key }
        - { key: anthropic-api-key,     path: anthropic-key }
        - { key: azure-openai-key,      path: azure-openai-key }
        - { key: azure-openai-endpoint, path: azure-openai-endpoint }
```

Then install from the vendored copy: `helm install vais-runtime ./vais-agents-runtime -n vais`.

**Pattern B — quick path (`kubectl patch` after `helm install`):**

```bash
helm install vais-runtime ./deploy/helm/vais-agents-runtime -n vais
kubectl patch deployment vais-runtime -n vais --patch-file - <<'EOF'
spec:
  template:
    spec:
      containers:
        - name: runtime
          volumeMounts:
            - name: llm-keys
              mountPath: /var/run/secrets/vais
              readOnly: true
      volumes:
        - name: llm-keys
          secret:
            secretName: vais-llm-keys
EOF
```

The patch is lost on the next `helm upgrade` unless you re-apply it. Acceptable for staging; not for anything you care about.

### 2.3 Reference the projected files in agent manifests

```yaml
spec:
  model:
    provider: openai
    id: gpt-4o
    apiKeyRef: secret://file/var/run/secrets/vais/openai-key
```

For Anthropic and Azure, the same pattern:

```yaml
# Anthropic
spec:
  model:
    provider: anthropic
    id: claude-3-7-sonnet
    apiKeyRef: secret://file/var/run/secrets/vais/anthropic-key

# Azure OpenAI — both apiKeyRef and baseUrlRef required
spec:
  model:
    provider: azure-openai
    id: gpt-4o-prod-deployment   # Azure deployment name, not model id
    apiKeyRef: secret://file/var/run/secrets/vais/azure-openai-key
    baseUrlRef: secret://file/var/run/secrets/vais/azure-openai-endpoint
```

## 3. Multiple providers in one runtime

There is no per-runtime "default provider." Each agent declares its own `spec.model`, which means **one runtime can serve any mix of providers** — one tenant's agents on OpenAI, another's on Anthropic, a third's on Azure — as long as each manifest's `apiKeyRef` resolves to a real key.

```yaml
# agent-team-a.yaml — OpenAI
spec:
  model:
    provider: openai
    id: gpt-4o
    apiKeyRef: secret://file/var/run/secrets/vais/openai-key

---
# agent-team-b.yaml — Anthropic
spec:
  model:
    provider: anthropic
    id: claude-3-7-sonnet
    apiKeyRef: secret://file/var/run/secrets/vais/anthropic-key
```

Apply both: `vais apply -f agent-team-a.yaml -f agent-team-b.yaml`.

The runtime caches provider clients by `ModelSpec` record equality, so two agents declaring the same `(provider, id, apiKeyRef, baseUrlRef, ...)` share one SDK client. Two agents declaring the same provider + id with **different** key refs get isolated clients — useful for per-tenant key rotation.

## 4. Custom endpoints — vLLM / Ollama / LiteLLM / OpenRouter / Azure

The `openai` provider accepts an optional `baseUrlRef`. When set, the SDK client points at that endpoint instead of `api.openai.com`. Any OpenAI-compatible service works without code changes.

**Self-hosted vLLM:**

```yaml
spec:
  model:
    provider: openai
    id: meta-llama/Llama-3-70B-Instruct       # vLLM model name
    apiKeyRef: secret://env/VLLM_API_KEY      # vLLM accepts any non-empty string by default
    baseUrlRef: secret://env/VLLM_ENDPOINT    # e.g. http://vllm.ml.svc:8000/v1
```

**Ollama (OpenAI-compat bridge):**

```yaml
spec:
  model:
    provider: openai
    id: llama3.1:70b                           # Ollama tag
    apiKeyRef: secret://env/OLLAMA_API_KEY     # Set to any non-empty string
    baseUrlRef: secret://env/OLLAMA_ENDPOINT   # e.g. http://ollama.ml.svc:11434/v1
```

Ollama's native protocol is not directly supported — only the OpenAI-compatible `/v1` endpoint.

**LiteLLM / Portkey / OpenRouter (gateway proxies):**

```yaml
spec:
  model:
    provider: openai
    id: anthropic/claude-3-7-sonnet            # router model id
    apiKeyRef: secret://file/var/run/secrets/vais/litellm-key
    baseUrlRef: secret://env/LITELLM_ENDPOINT  # e.g. http://litellm.gateway.svc:4000/v1
```

**Azure OpenAI:** use the dedicated `azure-openai` provider, not `openai` + `baseUrlRef` — Azure's URL shape and authentication differ.

```yaml
spec:
  model:
    provider: azure-openai
    id: gpt-4o-prod                            # Azure deployment name
    apiKeyRef: secret://file/var/run/secrets/vais/azure-openai-key
    baseUrlRef: secret://file/var/run/secrets/vais/azure-openai-endpoint
```

## 5. Fallback pools — multiple keys per agent

The `Fallback` LLM gateway middleware tries each entry in a pool in order, falling over to the next on any exception. Useful for cross-provider redundancy, rate-limit headroom, or a "primary cloud → self-hosted fallback" pattern.

```yaml
apiVersion: vais.agents/v1
kind: LlmGatewayConfig
metadata:
  id: resilient-llm
  version: "1.0"
spec:
  middleware:
    - name: LlmLogging
    - name: Fallback
      params:
        pool:
          - provider: openai
            id: gpt-4o
            apiKeyRef: secret://file/var/run/secrets/vais/openai-key
          - provider: azure-openai
            id: gpt-4o-prod
            apiKeyRef: secret://file/var/run/secrets/vais/azure-openai-key
            baseUrlRef: secret://file/var/run/secrets/vais/azure-openai-endpoint
          - provider: openai
            id: meta-llama/Llama-3-70B-Instruct
            apiKeyRef: secret://env/VLLM_API_KEY
            baseUrlRef: secret://env/VLLM_ENDPOINT
```

Each pool entry is a **full ModelSpec** — provider, id, apiKeyRef, baseUrlRef, temperature, topP, maxTokens, responseFormat — so an Azure fallback or self-hosted last-line-of-defence works exactly like a top-level `spec.model` block.

Bind any agent to the config via `llmGatewayRef: resilient-llm`. On the streaming path, fallback commits to the first provider that delivers ≥1 delta; it does not retry a stream already in progress. See **[Wire the LLM gateway](../agent-developer/wire-the-llm-gateway.md)** for the full middleware catalog.

**Not supported today:** per-route key rotation within one provider (e.g. round-robin across five OpenAI keys for rate-limit headroom). `LlmRateLimit` caps the agent's overall rate; it doesn't rotate keys. The workaround is to declare each key as a separate pool entry and let Fallback rotate on rate-limit exceptions.

## 6. Custom resolver schemes

Add a `secret://keyvault/...` / `secret://aws-sm/...` / `secret://vault/...` scheme by implementing `ISecretResolver` and registering it in your runtime's composition root:

```csharp
public sealed class KeyVaultSecretResolver : ISecretResolver
{
    private readonly SecretClient _client;
    public KeyVaultSecretResolver(SecretClient client) => _client = client;

    public async ValueTask<string> ResolveAsync(string uri, CancellationToken ct = default)
    {
        // uri shape: secret://keyvault/<secret-name>
        var name = uri["secret://keyvault/".Length..];
        var response = await _client.GetSecretAsync(name, cancellationToken: ct);
        return response.Value.Value;
    }
}
```

Register alongside the built-in resolvers:

```csharp
services.AddSingleton<ISecretResolver>(sp =>
    new CompositeSecretResolver(new Dictionary<string, ISecretResolver>
    {
        ["env"]      = new EnvironmentSecretResolver(),
        ["file"]     = new FileSecretResolver(),
        ["keyvault"] = new KeyVaultSecretResolver(sp.GetRequiredService<SecretClient>()),
    }));
```

`CompositeSecretResolver` dispatches by URI scheme. Add caching at this layer if your secret store has latency budget (the default resolvers cache nothing; OpenAI/Anthropic factories only call `ResolveAsync` at activation time, so the hot path is unaffected).

## 7. Troubleshooting

| Symptom                                                                                   | Cause                                                                                                                          | Fix                                                                                                  |
|-------------------------------------------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------------|
| `400 urn:vais-agents:model-provider-unsupported` — "resolved to an empty value"           | The env var or file resolved to `""` or whitespace                                                                             | Check the env / file content; for K8s Secrets, confirm the key exists in the Secret                  |
| Same URN — "could not be resolved"                                                        | Env var missing, file path missing, or unknown scheme                                                                          | `kubectl describe pod` to confirm the volume mounted; `printenv \| grep <VAR>` inside the container  |
| Same URN — "not a valid URI" (on `baseUrlRef`)                                            | `baseUrlRef` resolved to something that isn't an absolute URL                                                                  | Include the protocol (`http://`, `https://`) and `/v1` path suffix for OpenAI-compatible endpoints   |
| `No IModelProviderFactory registered for provider 'X'`                                    | Provider key is not one of `openai` / `anthropic` / `azure-openai` and no custom factory is registered                         | Either fix the typo in `spec.model.provider`, or register a custom `IModelProviderFactory`           |
| Anthropic factory throws on activation despite `ANTHROPIC_API_KEY` being set              | Anthropic does **not** fall back to env-var-by-convention; `apiKeyRef` is required                                             | Add `apiKeyRef: secret://env/ANTHROPIC_API_KEY` explicitly                                           |
| Fallback pool entry with `baseUrlRef` silently degrades to base endpoint                  | Pre-fix runtime (< commit `a03d1f8`) only forwarded `provider`/`id`/`apiKeyRef`                                                | Upgrade to runtime ≥ v0.17 — see [CHANGELOG](../../CHANGELOG.md)                                     |

## Known limitations

- **The Helm chart does not yet expose `extraEnv` / `extraEnvFrom` / `extraVolumes` knobs.** Until those land, the production K8s path requires either vendoring the chart (recommended) or `kubectl patch` after install (acceptable for staging). Track the enhancement at `deploy/helm/vais-agents-runtime/values.yaml` — proposed addition: a top-level `extraEnvFrom: []` array forwarded into the container's `envFrom:`.
- **No per-key rate-limit rotation.** `LlmRateLimit` caps the agent, not the key. Use a multi-entry Fallback pool to fan out across keys.
- **Custom provider factories ship via DI, not via manifest.** Adding Bedrock / Gemini / a native Ollama protocol provider requires writing a small `IModelProviderFactory` and registering it in the runtime's composition root. There is no `providers.yaml` registration kind.
- **No `secret://` resolver caching by default.** Activation-time-only resolution makes this a non-issue for the LLM hot path, but custom factories that re-resolve per request should wrap their resolver in a cache.

## What you configured

- The `secret://` indirection — manifests reference secrets, runtime resolves them.
- Env-backed secrets for local dev, file-backed secrets for production K8s.
- Multi-provider agents in one runtime, with isolated per-agent keys.
- Custom OpenAI-compatible endpoints (vLLM / Ollama / LiteLLM / OpenRouter / Azure).
- A Fallback pool that fans out across providers, keys, and endpoints.
- An extension point for KeyVault / AWS Secrets Manager / Vault via custom `ISecretResolver`.

## Related

- **[Reference → manifest schema](../reference/manifest-schema.md#specmodel--modelspec)** — definitive ModelSpec field reference + secret URI format.
- **[Concepts → declarative agents](../concepts/declarative-agents.md)** — agent manifest anatomy in context.
- **[Wire the LLM gateway](../agent-developer/wire-the-llm-gateway.md)** — gateway middleware catalog including Fallback semantics.
- **[Deploy the runtime on Kubernetes](deploy-runtime-on-kubernetes.md)** — Helm chart walkthrough this guide builds on.
- **[Reference → runtime configuration](../reference/runtime-configuration.md)** — every env var + `appsettings.json` knob recognised by the runtime.
