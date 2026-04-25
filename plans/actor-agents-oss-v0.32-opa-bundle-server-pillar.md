# v0.32 — OPA bundle-server + signature verification

Polish pillar closing the deferred item from v0.14:
> **OPA bundle-server + signature verification.** Policies are loaded
> from disk / ConfigMap today; there is no signed-bundle pipeline.
> Source: v0.14 findings (2026-04-20). Next step: post-v0.14 polish
> pillar once a consumer asks.

The `Vais.Agents.Control.Policy.Opa` adapter is **untouched** — it is
a pure-HTTP client to OPA's data API. Bundle distribution and signature
verification are OPA-internal concerns driven by OPA's own
`bundles:` + `keys:` configuration. This pillar wires that
configuration into the Helm chart and ships a working sample.

Created 2026-04-25.

---

## What changes

### 1. Helm chart `opa.bundle.*` sub-values block

`deploy/helm/vais-agents-runtime/values.yaml` gains a nested `bundle:`
block under `opa:`:

```yaml
opa:
  bundle:
    enabled: false
    url: ""                      # required when enabled
    resource: /bundle.tar.gz
    polling:
      minDelaySeconds: 60
      maxDelaySeconds: 120
    serviceAuthTokenSecret: ""   # K8s Secret carrying a bearer token
    serviceAuthTokenSecretKey: token
    signing:
      enabled: false
      keyId: vais-bundle-key
      algorithm: RS256            # RS256 | ES256 | HS256
      existingSecret: ""          # K8s Secret carrying the PEM/HMAC key
      existingSecretKey: key.pem
```

`bundle.enabled` is only meaningful when the chart is running a
pod-local OPA sidecar (i.e. `opa.enabled=true` and `opa.baseUrl` is
empty). A consumer pointing at an external OPA manages that OPA's
bundle config themselves.

### 2. OPA config ConfigMap template

New `templates/configmap-opa-config.yaml` — rendered only when
`opaBundleMode=true` (= sidecar mode + bundle enabled). Emits an
OPA-native `config.yaml` with:

- `services.vais-bundle-server.url` from `opa.bundle.url`
- Optional `credentials.bearer.token: "${BUNDLE_SERVER_TOKEN}"` when
  a `serviceAuthTokenSecret` is set
- `bundles.vais-agents` with `resource`, `polling.min/max_delay_seconds`
- Optional `bundles.vais-agents.signing.keyid` when `signing.enabled`
- Optional `keys.<keyId>` with `algorithm` + `key: "${OPA_BUNDLE_SIGNING_KEY}"`
  when `signing.enabled`

Environment variable substitution (`${...}`) is resolved by OPA at
startup — the values come from `secretKeyRef` env injections in the
sidecar, not from Helm. This keeps secrets out of the ConfigMap.

### 3. `_helpers.tpl` additions

- `vais-agents-runtime.opaBundleMode` — `"true"` when
  `opa.enabled && !opa.baseUrl && opa.bundle.enabled`
- `vais-agents-runtime.opaConfigMapName` — name for the OPA config CM

### 4. `deployment.yaml` OPA sidecar changes (bundle mode)

When `opaBundleMode=true`:

- OPA `args` switch from `["run", "--server", "--addr=:8181",
  "--log-format=json", "/policies"]` to `["run", "--server",
  "--addr=:8181", "--log-format=json",
  "--config-file=/opa-config/config.yaml"]`
- Add `opa-config` volumeMount (from the new ConfigMap)
- Add `opa-tmp` emptyDir volumeMount at `/tmp` (OPA downloads bundles
  to a temp directory; required with `readOnlyRootFilesystem: true`)
- Conditionally inject `OPA_BUNDLE_SIGNING_KEY` env var from
  `signing.existingSecret`
- Conditionally inject `BUNDLE_SERVER_TOKEN` env var from
  `serviceAuthTokenSecret`

When `opaBundleMode=false` (ConfigMap mode, unchanged):

- Original args still include `/policies`; `opa-policy` volume
  still mounted; no new volumes

### 5. Sample `samples/opa-bundle-server/`

| File | Purpose |
|---|---|
| `Dockerfile` | Nginx-based bundle server; serves `/bundle.tar.gz` from `/bundles/` |
| `nginx.conf` | Minimal config: `location /bundle.tar.gz { ... }` |
| `bundle/vais-agents.rego` | Starter allow-all Rego policy to bundle |
| `sign-bundle.sh` | Uses `opa sign` to produce a signed bundle + writes signing key pair |
| `docker-compose.yaml` | Local-dev stack: bundle-server + OPA + verifies signature |
| `README.md` | Full walkthrough: build bundle → sign → serve → OPA verifies → query |

### 6. `samples/opa-sidecar/README.md` update

Cross-link to the bundle-server sample at the bottom; note that the
Helm chart now supports `opa.bundle.*` natively (no manual patch
required).

---

## Scope boundary

**Not in v0.32:**

- Changes to `Vais.Agents.Control.Policy.Opa` (.NET adapter) — none needed
- `deploy/helm/vais-agents-operator/` OPA integration — separate §11 item
- OPA decision-log forwarding — separate §Observability item
- Embedded Wasm adapter, Envoy ext-authz — separate §11 items
- Multi-engine composition helper — consumer concern

---

## Delivery

Single commit: `feat(policy): v0.32 OPA bundle-server + signature
verification — Helm chart opa.bundle.* block + nginx sample + sign-bundle.sh`

Plus milestone log update commit.

---

## Progress log

- 2026-04-25 — plan created. Scope locked: Helm chart `opa.bundle.*` +
  OPA config ConfigMap + deployment.yaml sidecar switch + nginx bundle
  server sample + sign/compose scripts + README. No .NET adapter changes.
  Version: v0.32. Single-commit delivery.

- 2026-04-25 — implementation complete. 8 files changed / created:
  `values.yaml` (+`opa.bundle.*` block), `_helpers.tpl` (+2 helpers),
  `configmap-opa-config.yaml` (new), `configmap-opa.yaml` (bundle-mode guard),
  `deployment.yaml` (bundle-mode sidecar path + env vars + volumes),
  `samples/opa-bundle-server/` (Dockerfile + nginx.conf + bundle/vais-agents.rego +
  sign-bundle.sh + docker-compose.yaml + README), `samples/opa-sidecar/README.md`
  (cross-link added). Five `helm template` smoke tests all pass:
  default / ConfigMap mode / bundle (unsigned) / bundle+signing / bundle+signing+auth.
  Milestone log + deferred backlog updated. **Pillar closed.**
