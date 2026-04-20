# vais-agents-operator Helm chart

Deploys the Vais.Agents Kubernetes operator (v0.13.0-preview) — reconciles
`Agent` (`vais.io/v1alpha1`) custom resources against the v0.6 HTTP
control plane via `AgentControlPlaneClient`.

## Quick start

Build the container image locally (first-time only):

```bash
cd oss/agentic
docker build \
  -t vais-agents-operator:0.13.0-preview \
  -f src/Vais.Agents.Control.KubernetesOperator.Host/Dockerfile \
  .
```

Install the chart (single-replica, ClusterRole + ClusterRoleBinding, CRD
installed as a pre-install hook):

```bash
helm install vais-agents-operator ./deploy/helm/vais-agents-operator \
  --namespace vais-agents --create-namespace \
  --set image.repository=vais-agents-operator \
  --set image.tag=0.13.0-preview \
  --set controlPlane.baseUrl=http://host.docker.internal:5080
```

Apply a sample `Agent` CR (requires the runtime's v0.6 HTTP control plane
reachable at `controlPlane.baseUrl`):

```bash
kubectl apply -f - <<'EOF'
apiVersion: vais.io/v1alpha1
kind: Agent
metadata:
  name: chat-assistant
  namespace: default
  annotations:
    vais.io/tenant-id: tenant-42
spec:
  agentId: chat-assistant
  version: v1
  handler:
    typeName: Vais.Agents.Samples.ChatAgent
  protocols:
    - kind: Http
  tools:
    - name: weather
  preserveOnDelete: false
EOF

kubectl get vagent -n default
kubectl describe vagent/chat-assistant -n default
```

## Values

| Key | Default | Description |
|---|---|---|
| `image.repository` | `vais-agents-operator` | Container image. |
| `image.tag` | `0.13.0-preview` | Image tag. |
| `image.pullPolicy` | `IfNotPresent` | Kubernetes pull policy. |
| `replicaCount` | `1` | Single-replica MVP. Leader-election deferred. |
| `controlPlane.baseUrl` | `""` (**required**) | v0.6 HTTP control-plane URL. |
| `controlPlane.audience` | `vais-agents-runtime` | JWT audience on the projected SA token. |
| `auth.mode` | `ServiceAccount` | `ServiceAccount` (projected token) or `ClientCredentials`. |
| `auth.tokenPath` | `/var/run/secrets/tokens/vais-runtime-token` | Projected token mount path. |
| `auth.tokenExpirationSeconds` | `3600` | Kubelet rotates before this expiry. |
| `watchNamespaces` | `[]` | Empty = cluster-wide. Narrow for per-tenant installs. |
| `rbac.create` | `true` | Create SA + ClusterRole + ClusterRoleBinding. |
| `rbac.installCrd` | `true` | Install CRD as a `helm.sh/hook: pre-install,pre-upgrade`. |
| `resources.*` | `50m / 64Mi requests, 128Mi limit` | Tuned for a lean reconcile loop. |

## Uninstall

```bash
helm uninstall vais-agents-operator --namespace vais-agents
# CRD retained by default (helm.sh/hook-delete-policy: before-hook-creation).
# Delete explicitly if you want the agents.vais.io CRD removed:
kubectl delete crd agents.vais.io
```

## Known limitations (v0.13.0-preview)

- **CRD schema uses `x-kubernetes-preserve-unknown-fields: true` at the spec
  level**: server-side validation is loose; the runtime validates the
  projected manifest on every upsert. A later pillar tightens the schema
  once KubeOps' auto-transpiler handles TimeSpan properties.
- **`spec.secretRefs` is validation-only**: the operator resolves K8s
  Secrets (and fails reconcile with `ManifestValid=False` if any are
  missing) but does NOT inject resolved values into the manifest envelope.
  Use `env:` / `file:` URIs in manifest fields and the runtime's existing
  `ISecretResolver` composite. Runtime-side inline-secret wire format is
  deferred to a future pillar.
- **Single-replica / no leader election**: don't run more than one replica.
- **No automated cluster tests in CI**: controller logic is covered by
  unit tests; cluster-side verification is manual (`docker-desktop`
  kubernetes is a fine target).
