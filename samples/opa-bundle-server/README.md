# OPA bundle-server sample

This sample shows how to distribute and optionally sign OPA policies as a
**bundle** served over HTTP — rather than mounting a ConfigMap into the
OPA sidecar pod. It ships a minimal nginx bundle server, a signing helper
script, and a Docker Compose dev stack.

> For the simple ConfigMap-mount pattern see
> [`samples/opa-sidecar/README.md`](../opa-sidecar/README.md).

---

## Why bundles?

| ConfigMap mount | Bundle server |
|---|---|
| Policies live in a ConfigMap; update requires a `kubectl apply` + OPA watch-restart | Policies live in a versioned archive on a server; OPA polls and hot-swaps without pod restart |
| No signing story | Bundles are signed with RS256/ES256/HS256; OPA rejects tampered or unsigned bundles |
| Simple — good for dev and single-cluster single-team | Better for multi-cluster, external policy registry, CI-published policies |

---

## Directory layout

```
samples/opa-bundle-server/
├── bundle/
│   └── vais-agents.rego      # Starter Rego policy included in the bundle
├── Dockerfile                # nginx image that serves bundle.tar.gz
├── nginx.conf                # Minimal nginx configuration
├── sign-bundle.sh            # Builds + (optionally) signs the bundle
├── docker-compose.yaml       # Local dev: bundle-server + OPA sidecar
└── README.md
```

---

## Quick start (local dev, unsigned)

```bash
# 1. Build an unsigned bundle
./sign-bundle.sh

# 2. Start the stack
docker compose up --build

# 3. Query OPA (should return allowed=true for an authenticated Invoke)
curl -s http://localhost:8181/v1/data/vais/agents/allow \
     -H 'Content-Type: application/json' \
     -d '{
           "input": {
             "schemaVersion": "1",
             "operation": "Invoke",
             "principal": {"id": "alice", "tenantId": "t1"},
             "agent": null
           }
         }' | jq .
```

---

## Signed bundle (production)

### 1. Build and sign the bundle

```bash
./sign-bundle.sh --sign
```

Outputs:
- `bundle.tar.gz` — signed OPA bundle
- `bundle-signing-pub.pem` — RS256 public key (**commit this**)
- `bundle-signing-key.pem` — RS256 private key (**never commit**)

### 2. Create a Kubernetes Secret with the public key

```bash
kubectl create secret generic opa-bundle-signing-key \
  --from-file=key.pem=bundle-signing-pub.pem \
  --namespace vais-agents
```

### 3. Deploy the bundle server

Build and push the nginx bundle-server image:

```bash
docker build -t registry.example.com/opa-bundle-server:v1 .
docker push registry.example.com/opa-bundle-server:v1
```

Deploy as a Kubernetes Deployment + Service (example):

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: opa-bundle-server
  namespace: vais-agents
spec:
  replicas: 2
  selector:
    matchLabels:
      app: opa-bundle-server
  template:
    metadata:
      labels:
        app: opa-bundle-server
    spec:
      containers:
        - name: bundle-server
          image: registry.example.com/opa-bundle-server:v1
          ports:
            - containerPort: 8888
---
apiVersion: v1
kind: Service
metadata:
  name: opa-bundles
  namespace: vais-agents
spec:
  selector:
    app: opa-bundle-server
  ports:
    - port: 8888
      targetPort: 8888
```

### 4. Deploy the Vais runtime with bundle + signing enabled

```bash
helm upgrade --install vais-runtime deploy/helm/vais-agents-runtime \
  --namespace vais-agents \
  --set opa.enabled=true \
  --set opa.bundle.enabled=true \
  --set opa.bundle.url=http://opa-bundles.vais-agents.svc:8888 \
  --set opa.bundle.signing.enabled=true \
  --set opa.bundle.signing.existingSecret=opa-bundle-signing-key
```

The Helm chart wires:
- An `opa-config` ConfigMap containing OPA's `config.yaml` (bundle URL +
  polling interval + signing key reference)
- The signing key as `OPA_BUNDLE_SIGNING_KEY` env var on the OPA sidecar
  (injected via `secretKeyRef`)
- OPA sidecar args switched to `--config-file=/opa-config/config.yaml`
  (bundle polling mode instead of ConfigMap-mount mode)

### 5. Verify OPA loaded the bundle

```bash
# Port-forward directly to the OPA sidecar
kubectl port-forward deployment/vais-runtime 8181:8181 -n vais-agents &

# Check bundle revision in OPA status
curl -s http://localhost:8181/v1/status | jq '.result.bundles'
```

You should see `{"vais-agents": {"revision": "...", "active_revision": "...",
"last_successful_download": "..."}}`.

---

## Authenticated bundle server

If your bundle server requires a bearer token (e.g. a private registry),
store the token in a Kubernetes Secret:

```bash
kubectl create secret generic opa-bundle-token \
  --from-literal=token=<your-token> \
  --namespace vais-agents
```

Then add `--set` flags:

```bash
--set opa.bundle.serviceAuthTokenSecret=opa-bundle-token
```

The chart injects the token as `BUNDLE_SERVER_TOKEN` env var on the OPA
sidecar and embeds `credentials.bearer.token: "${BUNDLE_SERVER_TOKEN}"` in
the OPA config.

---

## Helm values reference (bundle block)

```yaml
opa:
  enabled: true
  bundle:
    enabled: true
    url: http://opa-bundles.vais-agents.svc:8888
    resource: /bundle.tar.gz       # default
    polling:
      minDelaySeconds: 60          # default
      maxDelaySeconds: 120         # default
    serviceAuthTokenSecret: ""     # K8s Secret name (bearer token)
    serviceAuthTokenSecretKey: token
    signing:
      enabled: true
      keyId: vais-bundle-key       # must match --signing-key-id in sign-bundle.sh
      algorithm: RS256             # RS256 | ES256 | HS256
      existingSecret: opa-bundle-signing-key
      existingSecretKey: key.pem   # key inside the Secret
```

---

## CI publishing pattern

Integrate bundle signing into your CI pipeline:

```bash
# In CI (after running OPA tests):
opa build bundle/ --output bundle.tar.gz
opa sign bundle.tar.gz \
    --signing-key "$OPA_SIGNING_KEY_PEM" \   # from CI secret
    --signing-key-id vais-bundle-key \
    --bundle \
    --output bundle.tar.gz

# Push to your bundle registry (S3, GCS, nginx, OCI, ...)
aws s3 cp bundle.tar.gz s3://my-bucket/vais/bundle.tar.gz
```

OPA polls on the configured interval and picks up the new signed bundle
without any pod restart. Set `polling.minDelaySeconds` to match your
desired policy propagation latency.

---

## Rego authoring

Policies in `bundle/` follow the same patterns as `samples/opa-policies/`.
See the [OPA policy samples README](../opa-policies/README.md) for
authoring patterns, rule composition, and the full input schema.
