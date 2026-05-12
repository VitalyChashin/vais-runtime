# IdentityOidc

Two-runtime cross-runtime graph secured end-to-end with JWT authentication (v0.29 + v0.30). Runtime A orchestrates the graph; Runtime B hosts the callee agent and validates inbound tokens. Runtime A exchanges or forwards a bearer token on each remote invocation.

**Concepts:** [configure-oidc-identity](../../docs/guides/configure-oidc-identity.md), [cross-runtime graphs](../../docs/concepts/cross-runtime-graphs.md).
**Needs API key:** yes — `OPENAI_API_KEY` on both runtimes.
**Code:** 0 lines — YAML manifests only.

---

## What this shows

- `VAIS_JWT_AUTHORITY` on Runtime B — enables JWT bearer validation using OIDC discovery.
- `VAIS_SA_PRINCIPAL_MAPPER=true` on Runtime B — splits `system:serviceaccount:<ns>:<sa>` `sub` claims into `AgentPrincipal.Id` + `AgentPrincipal.TenantId`.
- `Vais:RemoteRuntimes:<key>:IdentityMode` on Runtime A — controls the outbound credential sent to Runtime B. Three modes:
  - `Forward` — pass the inbound token verbatim (same IdP on both runtimes).
  - `ServiceAccount` — read a Kubernetes projected SA token from disk.
  - `TokenExchange` — RFC 8693 token exchange via Keycloak / Entra (different trust domains).
- A graph node with `ref.runtimeUrl` pointing to Runtime B — the orchestrator on A calls B over HTTP with the configured credential.

---

## Quickstart

### 1. Build the runtime image

```bash
cd oss/agentic
docker build -f src/Vais.Agents.Runtime.Host/Dockerfile -t vais-agents-runtime:local .
```

### 2. Start an OIDC-compatible IdP (dev only)

This sample uses Keycloak in dev mode. Skip if you already have an IdP.

```bash
docker run -d --name keycloak -p 9090:8080 \
  -e KEYCLOAK_ADMIN=admin -e KEYCLOAK_ADMIN_PASSWORD=admin \
  quay.io/keycloak/keycloak:24.0 start-dev
```

Open `http://localhost:9090`, log in as `admin/admin`, and create:
- **Realm:** `vais-dev`
- **Client:** `vais-runtime-a` (service account enabled, client credentials flow, `vais-runtime-b` in the audience mapper)
- **Client:** `vais-runtime-b` (bearer-only)

Token exchange endpoint: `http://localhost:9090/realms/vais-dev/protocol/openid-connect/token`

### 3. Start Runtime B (callee)

```bash
docker run -d --name runtime-b -p 8081:8080 \
  -e OPENAI_API_KEY="$OPENAI_API_KEY" \
  -e VAIS_JWT_AUTHORITY="http://localhost:9090/realms/vais-dev" \
  -e VAIS_JWT_AUDIENCE="vais-runtime-b" \
  -e VAIS_SA_PRINCIPAL_MAPPER="true" \
  vais-agents-runtime:local
```

`VAIS_SA_PRINCIPAL_MAPPER=true` activates `ServiceAccountPrincipalMapper`. Without a Kubernetes SA token, the `sub` is used verbatim as `AgentPrincipal.Id`.

### 4. Start Runtime A (caller — TokenExchange mode)

```bash
docker run -d --name runtime-a -p 8080:8080 \
  -e OPENAI_API_KEY="$OPENAI_API_KEY" \
  -e VAIS_JWT_AUTHORITY="http://localhost:9090/realms/vais-dev" \
  -e VAIS_JWT_AUDIENCE="vais-runtime-a" \
  -e "Vais__RemoteRuntimes__http://localhost:8081__IdentityMode=TokenExchange" \
  -e "Vais__RemoteRuntimes__http://localhost:8081__TokenExchangeEndpoint=http://localhost:9090/realms/vais-dev/protocol/openid-connect/token" \
  -e "Vais__RemoteRuntimes__http://localhost:8081__ClientId=vais-runtime-a" \
  -e "Vais__RemoteRuntimes__http://localhost:8081__ClientSecret=<your-client-secret>" \
  -e "Vais__RemoteRuntimes__http://localhost:8081__Audience=vais-runtime-b" \
  vais-agents-runtime:local
```

> **Simpler alternative (same IdP, Forward mode):** Replace the five `Vais__RemoteRuntimes__*` variables with a single `Vais__RemoteRuntimes__http://localhost:8081__IdentityMode=Forward`. Runtime A then passes the caller's inbound token through unchanged.

Verify both runtimes are healthy:

```bash
curl http://localhost:8080/healthz && curl http://localhost:8081/healthz
# ok  ok
```

### 5. Configure CLI contexts

```bash
vais config set-context runtime-a --server http://localhost:8080
vais config set-context runtime-b --server http://localhost:8081
```

Obtain a token for runtime-a from your IdP (Keycloak example):

```bash
TOKEN=$(curl -s -X POST \
  http://localhost:9090/realms/vais-dev/protocol/openid-connect/token \
  -d "grant_type=client_credentials&client_id=vais-runtime-a&client_secret=<secret>" \
  | jq -r .access_token)
```

### 6. Apply manifests

```bash
# callee-agent lives on Runtime B
vais config use-context runtime-b
vais apply -f samples/IdentityOidc/callee-agent.yaml --token "$TOKEN"

# caller-graph and caller-agent live on Runtime A
vais config use-context runtime-a
vais apply -f samples/IdentityOidc/caller-agent.yaml --token "$TOKEN"
vais apply -f samples/IdentityOidc/caller-graph.yaml --token "$TOKEN"
```

### 7. Invoke the graph

```bash
vais config use-context runtime-a
vais invoke-graph caller-graph \
  --initial-state '{"input": "What is the capital of France?"}' \
  --token "$TOKEN"
```

Runtime A will:
1. Start the graph → route the `ask-callee` node to Runtime B (`runtimeUrl: http://localhost:8081`).
2. Perform a token exchange (or forward) so the request to Runtime B carries a valid `vais-runtime-b`-audience token.
3. Runtime B validates the token against its OIDC authority, extracts the principal, and runs `callee-agent`.
4. The result flows back through Runtime A → graph completes.

### 8. Verify principal extraction on Runtime B

```bash
# With ServiceAccountPrincipalMapper=true, a K8s SA token sub like
#   system:serviceaccount:tenant-a:vais-worker
# produces:
#   AgentPrincipal.Id       = "system:serviceaccount:tenant-a:vais-worker"
#   AgentPrincipal.TenantId = "tenant-a"
#
# For a regular client-credentials token (non-SA sub), TenantId comes from
# the tid / tenant_id claim (or null if absent).
docker logs runtime-b 2>&1 | grep "principal"
```

### 9. Clean up

```bash
vais config use-context runtime-a
vais delete-graph caller-graph
vais delete caller-agent

vais config use-context runtime-b
vais delete callee-agent

docker rm -f runtime-a runtime-b keycloak
```

---

## Manifests in this sample

### `callee-agent.yaml` — applied to Runtime B

A standard declarative agent. All OIDC enforcement is in the runtime host, not in the manifest.

### `caller-agent.yaml` — applied to Runtime A

A second declarative agent on Runtime A (used for standalone testing only; not referenced by the graph).

### `caller-graph.yaml` — applied to Runtime A

```yaml
spec:
  nodes:
    - id: ask-callee
      kind: Agent
      ref:
        id: callee-agent
        version: "1.0"
        runtimeUrl: http://localhost:8081   # ← dispatches to Runtime B
```

The `runtimeUrl` key must match the key used under `Vais:RemoteRuntimes` on Runtime A.

---

## Identity modes quick reference

| Mode | When to use | Runtime A env |
|---|---|---|
| `Forward` | Same IdP on both runtimes; zero-config | `IdentityMode=Forward` |
| `ServiceAccount` | Runtime A runs on K8s with a projected SA volume | `IdentityMode=ServiceAccount` + `ServiceAccountTokenPath=...` |
| `TokenExchange` | Different trust domains; RFC 8693-compatible STS | `IdentityMode=TokenExchange` + `TokenExchangeEndpoint`, `ClientId`, `ClientSecret`, `Audience` |

---

## Environment variables

| Variable | Required on | Purpose |
|---|---|---|
| `OPENAI_API_KEY` | Both | Forwarded to the OpenAI model provider |
| `VAIS_JWT_AUTHORITY` | Both | OIDC discovery URL — enables inbound JWT validation |
| `VAIS_JWT_AUDIENCE` | Both (optional) | Restricts accepted `aud` claim |
| `VAIS_SA_PRINCIPAL_MAPPER` | Runtime B | Split K8s SA `sub` into Id + TenantId |
| `Vais__RemoteRuntimes__<url>__IdentityMode` | Runtime A | How A authenticates to B |
| `Vais__RemoteRuntimes__<url>__TokenExchangeEndpoint` | Runtime A | STS token exchange endpoint (TokenExchange mode) |
| `Vais__RemoteRuntimes__<url>__ClientId` | Runtime A | Client ID for token exchange |
| `Vais__RemoteRuntimes__<url>__ClientSecret` | Runtime A | Client secret for token exchange (use a secrets manager in production) |
| `Vais__RemoteRuntimes__<url>__Audience` | Runtime A | Target audience for exchanged token |

---

## See also

- [docs/guides/configure-oidc-identity.md](../../docs/guides/configure-oidc-identity.md) — full reference for all identity config
- [docs/concepts/cross-runtime-graphs.md](../../docs/concepts/cross-runtime-graphs.md) — `runtimeUrl`, `RemoteAgentInvocationException`, streaming
- [docs/guides/compose-a-graph-across-runtimes.md](../../docs/guides/compose-a-graph-across-runtimes.md) — step-by-step cross-runtime guide (no auth)
- [docs/guides/gate-agents-with-opa.md](../../docs/guides/gate-agents-with-opa.md) — OPA policy enforcement after principal extraction
- [samples/graph-cross-runtime](../graph-cross-runtime) — cross-runtime graph without OIDC
