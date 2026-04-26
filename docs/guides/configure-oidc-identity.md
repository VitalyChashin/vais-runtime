# Configure OIDC identity

**v0.29 + v0.30.** This guide wires up the full JWT bearer pipeline on the runtime host and configures per-remote-runtime identity propagation for cross-runtime graph calls.

Two independent features are shipped together:

| Feature | Env var / config | Version |
|---|---|---|
| Inbound JWT validation + principal extraction | `VAIS_JWT_AUTHORITY` | v0.30 |
| Kubernetes SA principal mapper | `VAIS_SA_PRINCIPAL_MAPPER` | v0.30 |
| Outbound token propagation to remote runtimes | `Vais:RemoteRuntimes:<name>:IdentityMode` | v0.29 |

---

## 1. Inbound JWT validation

By default the runtime accepts all requests without authentication (localhost semantics). Set `VAIS_JWT_AUTHORITY` to enable the full JWT bearer pipeline:

```bash
# docker-compose or Kubernetes env
VAIS_JWT_AUTHORITY=https://keycloak.example.com/realms/my-realm
```

When set, `CompositionRoot` wires `UseAuthentication()` + `UseAuthorization()` + `UseAgentControlPlanePrincipalMapping()` on the middleware pipeline. The OIDC discovery document at `{Authority}/.well-known/openid-configuration` is fetched at startup; the JWKS is auto-refreshed on a 24-hour cadence.

**Audience restriction (optional):**

```bash
VAIS_JWT_AUDIENCE=vais-agents-api
```

When set, the `aud` claim is validated against this value. Leave unset to accept tokens for any audience from the authority.

**What is extracted from the JWT:**

| JWT claim | Maps to |
|---|---|
| `sub` | `AgentPrincipal.Id` |
| `tid` or `tenant_id` | `AgentPrincipal.TenantId` |
| `scope` or `scp` (space-separated) | `AgentPrincipal.Scopes` |

Clock skew tolerance defaults to 30 seconds. To adjust it programmatically (e.g. in integration tests), call `services.AddOidcAgentIdentity(o => o.ClockSkew = ...)` directly.

### Helm

```yaml
# values.yaml
auth:
  jwtAuthority: "https://keycloak.example.com/realms/my-realm"
  jwtAudience: "vais-agents-api"   # optional
```

---

## 2. Kubernetes ServiceAccount principal mapper

Workloads running inside Kubernetes often authenticate using projected ServiceAccount tokens. Their `sub` claim has the shape:

```
system:serviceaccount:<namespace>:<serviceaccount>
```

Enable `ServiceAccountPrincipalMapper` to split the namespace into `AgentPrincipal.TenantId`:

```bash
VAIS_SA_PRINCIPAL_MAPPER=true
```

With this flag, a token with `sub = system:serviceaccount:tenant-a:vais-worker` produces:

```
AgentPrincipal.Id       = "system:serviceaccount:tenant-a:vais-worker"
AgentPrincipal.TenantId = "tenant-a"
```

Without the flag, the default mapper sets `TenantId` from the `tenant_id` claim (or null if absent) — sufficient for non-K8s deployments.

> Both `VAIS_JWT_AUTHORITY` and `VAIS_SA_PRINCIPAL_MAPPER` must be set for the SA mapper to activate. Setting `VAIS_SA_PRINCIPAL_MAPPER=true` alone (without a JWT authority) has no effect.

### Helm

```yaml
auth:
  jwtAuthority: "https://keycloak.example.com/realms/my-realm"
  serviceAccountPrincipalMapper: true
```

---

## 3. Outbound identity propagation to remote runtimes

When a graph node has `runtimeUrl` set, the orchestrator calls the remote runtime via `IAgentRemoteInvoker`. Three propagation modes control what credential the invoker sends:

| Mode | Description | Use when |
|---|---|---|
| `Forward` (default) | Pass the inbound bearer token verbatim | Both runtimes trust the same IdP; zero-config |
| `ServiceAccount` | Read a Kubernetes projected SA token from disk | The calling runtime runs on K8s with a projected volume; the remote runtime accepts K8s SA tokens |
| `TokenExchange` | Exchange the subject token for an audience-scoped token via RFC 8693 | The two runtimes belong to different trust domains and you operate a compatible STS (Keycloak, Entra) |

Configure per target runtime under `Vais:RemoteRuntimes` in `appsettings.json` (or environment variable equivalents):

### Mode A — Forward (default)

No configuration needed. This is the v0.20 bearer-forwarding behaviour.

```json
{
  "Vais": {
    "RemoteRuntimes": {
      "runtime-b": {
        "IdentityMode": "Forward"
      }
    }
  }
}
```

The runtime URL key (`"runtime-b"` above) must match the `runtimeUrl` value used in the graph manifest node.

### Mode B — ServiceAccount

```json
{
  "Vais": {
    "RemoteRuntimes": {
      "https://runtime-b.internal": {
        "IdentityMode": "ServiceAccount",
        "ServiceAccountTokenPath": "/var/run/secrets/tokens/vais-runtime-token",
        "TokenCacheTtl": "00:05:00"
      }
    }
  }
}
```

`ServiceAccountTokenPath` defaults to `/var/run/secrets/tokens/vais-runtime-token`. Wire the projected volume in your Pod spec:

```yaml
volumes:
  - name: vais-token
    projected:
      sources:
        - serviceAccountToken:
            path: vais-runtime-token
            expirationSeconds: 3600
            audience: vais-agents-api
volumeMounts:
  - name: vais-token
    mountPath: /var/run/secrets/tokens
    readOnly: true
```

### Mode C — TokenExchange (RFC 8693)

```json
{
  "Vais": {
    "RemoteRuntimes": {
      "https://runtime-b.internal": {
        "IdentityMode": "TokenExchange",
        "TokenExchangeEndpoint": "https://keycloak.example.com/realms/my-realm/protocol/openid-connect/token",
        "ClientId": "vais-runtime-a",
        "ClientSecretRef": "secret://vais-runtime-a-client-secret",
        "Audience": "vais-runtime-b",
        "TokenCacheTtl": "00:05:00"
      }
    }
  }
}
```

`ClientSecretRef` is a `secret://` URI resolved by `ISecretResolver` at runtime — the raw secret value never appears in `appsettings.json`. The exchanged token is cached per `(runtimeUrl, subjectToken)` for `TokenCacheTtl` (default 5 minutes).

Required fields for `TokenExchange`:

| Field | Required |
|---|---|
| `TokenExchangeEndpoint` | Yes |
| `ClientId` | Yes |
| `ClientSecretRef` | Yes |
| `Audience` | No — defaults to the runtime URL |

---

## 4. Programmatic wiring (outside the runtime host)

If you host the control plane in your own ASP.NET Core app rather than using `Vais.Agents.Runtime.Host`, wire each piece manually:

```csharp
// appsettings-driven JWT validation (inbound)
services.AddAgentControlPlaneJwtAuth(o =>
{
    o.Authority = "https://keycloak.example.com/realms/my-realm";
    o.Audience  = "vais-agents-api";   // optional
});

// Kubernetes SA principal mapper (opt-in, register BEFORE AddAgentControlPlaneJwtAuth)
services.AddSingleton<IPrincipalMapper, ServiceAccountPrincipalMapper>();

// OIDC identity provider for outbound client_credentials grants
services.AddOidcAgentIdentity(o =>
{
    o.Authority = "https://keycloak.example.com/realms/my-realm";
    o.ClientId  = "vais-runtime-a";
    // ClockSkew, ValidateAudience, ValidateIssuer — all optional
});

// Remote invoker with per-runtime options
services.AddAgentRemoteInvoker(map =>
{
    map.Runtimes["https://runtime-b.internal"] = new RemoteRuntimeOptions
    {
        IdentityMode = RemoteIdentityMode.TokenExchange,
        TokenExchangeEndpoint = new Uri("https://keycloak.example.com/realms/my-realm/protocol/openid-connect/token"),
        ClientId = "vais-runtime-a",
        ClientSecretRef = "secret://vais-runtime-a-client-secret",
        Audience = "vais-runtime-b",
    };
});
```

Middleware order matters — call `UseAuthentication()` and `UseAuthorization()` before `UseAgentControlPlanePrincipalMapping()`.

---

## 5. Quick decision guide

```
Both runtimes trust the same IdP?
  └─ YES → Forward (zero-config)
  └─ NO  →
       Running on Kubernetes with projected SA volumes?
         └─ YES → ServiceAccount
         └─ NO  →
              Operate an RFC 8693-compatible STS (Keycloak, Entra)?
                └─ YES → TokenExchange
                └─ NO  → Deploy an OIDC-compliant IdP first
```

---

## See also

- [Cross-runtime graphs concept](../concepts/cross-runtime-graphs.md) — `runtimeUrl`, bearer forwarding, `RemoteAgentInvocationException`.
- [Compose a graph across runtimes](compose-a-graph-across-runtimes.md) — step-by-step cross-runtime graph walkthrough.
- [Gate agents with OPA](gate-agents-with-opa.md) — policy enforcement after principal extraction.
- [Deploy the runtime to Kubernetes](deploy-the-runtime-to-kubernetes.md) — Helm values reference, SA projected volumes.
