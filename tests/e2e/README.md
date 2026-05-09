# E2E Tests

Two suites exercising the container plugin lifecycle against real infrastructure — separate from the demo environment in `local-dev/`.

```
tests/e2e/
├── shared/
│   └── echo-plugin/        Minimal Python HTTP plugin (stdlib only; no LLM, no API keys)
├── docker/                 Docker-standalone topology suite
└── kubernetes/             Kubernetes topology suite
```

---

## Prerequisites

| Tool | Both suites | Docker only | K8s only |
|------|:-----------:|:-----------:|:--------:|
| dotnet 9 SDK | ✓ | ✓ | |
| docker CLI + daemon | ✓ | ✓ | ✓ |
| `vais` CLI on PATH | ✓ | ✓ | ✓ |
| Docker Desktop with Kubernetes enabled | | | ✓ |
| helm 3 on PATH | | | ✓ |
| kubectl on PATH | | | ✓ |

Build the echo plugin images once before running either suite (from the repo root `agentic/`):

```powershell
docker build -t vais-echo:test  tests/e2e/shared/echo-plugin/
docker tag  vais-echo:test vais-echo:test-v2
```

---

## Docker Suite

Tests the `standalone` topology: the runtime runs on the host machine and manages the echo plugin as a Docker container via `DockerContainerSupervisor`.

```powershell
cd agentic
.\tests\e2e\docker\run.ps1
```

**What it checks:**
1. Runtime starts and reaches `/healthz`
2. Runtime discovers `echo-plugin` from the container plugin directory and starts its Docker container
3. `vais plugin-status --output json` shows `topology=standalone`, `status=Ready`, correct image
4. `vais plugin-push --image vais-echo:test-v2 echo-plugin` reloads the plugin with the new image
5. Plugin returns to `Ready` with the updated image and `plugin-status` JSON is machine-readable

**Debug flag:**

```powershell
.\tests\e2e\docker\run.ps1 -KeepUp   # skips teardown; runtime stays running
```

**How the runtime is started:**

The script publishes `src/Vais.Agents.Runtime.Host` and runs the binary directly on the host with `VAIS_CONTAINER_PLUGINS_DIRECTORY` pointing at a temp directory containing the `plugin.yaml`. Running on the host (not in a Docker container) avoids Docker-in-Docker: `DockerContainerSupervisor` connects to the local daemon and `http://localhost:8080` is reachable from the runtime process.

---

## Kubernetes Suite

Tests the `kubernetes` topology: the runtime and echo plugin both run in the Docker Desktop Kubernetes cluster. The runtime uses `KubernetesContainerSupervisor` to patch the plugin Deployment image during `plugin-push`.

```powershell
cd agentic
.\tests\e2e\kubernetes\run.ps1
```

**What it checks:**
1. Runtime installed via Helm; echo plugin Deployment created by `vais plugin-deploy`
2. `vais plugin-status --output json` shows `topology=kubernetes`, `status=Ready`, `kubernetesDeploymentName` and `kubernetesNamespace` set
3. `vais plugin-push --image vais-echo:test-v2 echo-plugin` reports "rollout started" (HTTP 202)
4. JSON output confirms all topology fields populated correctly

**Debug flag:**

```powershell
.\tests\e2e\kubernetes\run.ps1 -KeepUp -Namespace vais-e2e-debug
```

**Namespace:**

```powershell
.\tests\e2e\kubernetes\run.ps1 -Namespace my-ns   # default: vais-e2e
```

---

## Design Decisions

**RBAC scope (cluster-wide):** The runtime's ClusterRole grants `patch` on `apps/v1/deployments` cluster-wide. This keeps setup simple for development and single-cluster scenarios. For multi-tenant production, replace with a namespaced `Role` + `RoleBinding` scoped to the plugin namespace. Tracked in the plan.

**imagePullPolicy: Never:** Docker Desktop shares its Docker daemon between the host and the Kubernetes cluster, so locally built images are immediately available to pods without a registry push. The K8s suite patches `imagePullPolicy: Never` on the echo plugin Deployment after `vais plugin-deploy` creates it. For CI environments where Docker and Kubernetes run separately, a local registry (e.g. `registry:2`) would be required. Tracked in the plan.

---

## What these tests do NOT cover

- Multi-node Orleans clustering
- OPA policy gating
- Langfuse / OTEL tracing
- Actual LLM invocations (no API keys required)
- Horizontal autoscaling
- CI/CD pipeline integration
