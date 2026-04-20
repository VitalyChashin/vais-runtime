# Vais.Agents deployment artefacts

Kubernetes manifests, Helm chart, and the operator's Dockerfile for
running the Vais.Agents operator against a v0.6 HTTP control plane.

## Contents

- [`crds/vais.io_agents.yaml`](crds/vais.io_agents.yaml) — standalone
  `CustomResourceDefinition` for `Agent` (`vais.io/v1alpha1`). Apply with
  `kubectl apply -f` for non-Helm installs.
- [`helm/vais-agents-operator/`](helm/vais-agents-operator/README.md) —
  Helm chart that installs the CRD + SA + RBAC + Deployment in one
  command. Helm-managed CRD install via a `helm.sh/hook: pre-install`.

## Quick start (docker-desktop Kubernetes)

```bash
# From the repo root:
cd oss/agentic

# 1. Build the operator container image locally.
docker build \
  -t vais-agents-operator:0.13.0-preview \
  -f src/Vais.Agents.Control.KubernetesOperator.Host/Dockerfile \
  .

# 2. Install the operator.
helm install vais-agents-operator ./deploy/helm/vais-agents-operator \
  --namespace vais-agents --create-namespace \
  --set controlPlane.baseUrl=http://host.docker.internal:5080

# 3. Apply a sample Agent CR.
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
EOF

# 4. Inspect status.
kubectl get vagent -n default
kubectl describe vagent/chat-assistant -n default
kubectl logs -l app.kubernetes.io/name=vais-agents-operator -n vais-agents
```

## Teardown

```bash
kubectl delete vagent --all --all-namespaces
helm uninstall vais-agents-operator --namespace vais-agents
# CRD is retained by default — delete explicitly if desired:
kubectl delete crd agents.vais.io
```

See the chart [README](helm/vais-agents-operator/README.md) for the full
values reference and v0.13.0-preview known limitations.

## Related

- [`../samples/opa-sidecar/README.md`](../samples/opa-sidecar/README.md) —
  wiring `Vais.Agents.Control.Policy.Opa` (v0.14) as an OPA sidecar
  alongside the operator / runtime for admission-control policy.
- [`../samples/opa-policies/README.md`](../samples/opa-policies/README.md) —
  ready-to-use Rego samples (tenant-scoped / model-provider allowlist /
  budget cap).
- [`../contracts/opa-input-schema.md`](../contracts/opa-input-schema.md) —
  v1 schema contract for Rego policies against the v0.6
  `IAgentPolicyEngine` verbs.
