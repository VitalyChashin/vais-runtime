# Harden Docker Container Plugins

This guide covers the two-phase Docker isolation model for container plugins
(P12 — Plugin Sandbox Contract). Phase 1 is on by default; Phase 2 is opt-in
and requires a topology change.

## Phase 1 — Host and process hardening (default)

Every Docker container plugin receives these defaults automatically via
`DockerContainerSupervisor`:

| Setting | Value | Effect |
|---|---|---|
| `HostIP` on port binding | `127.0.0.1` | Plugin port is not reachable outside the host |
| `ReadonlyRootfs` | `true` | Plugin code is immutable at runtime |
| `Tmpfs` at `/tmp` | 64 MiB | Writable scratch space without persistence |
| `CapDrop` | `ALL` | Every Linux capability dropped |
| `SecurityOpt` | `no-new-privileges:true` | Blocks setuid escalation |
| `Memory` / `MemorySwap` | 256 MiB (default) | Hard cap; OOM stays inside the container |
| `NanoCPUs` | 0.5 vCPU (default) | Plugin can't starve the host |
| `PidsLimit` | 128 | Fork-bomb defense |

### Override resource limits per plugin

Add a `resources` block to the plugin manifest:

```yaml
spec:
  resources:
    memory: 512Mi   # Kubernetes-style quantity (Mi, Gi, M, G)
    cpu: "1.0"      # vCPUs (or 500m for millicores)
    pidsLimit: 256
```

Operator-level caps are enforced by `ContainerPluginResourceBounds` in
`AddContainerPlugins(...)`. Manifest requests above the cap are clamped silently.

### Plugin Dockerfile requirements

Phase 1 sets `ReadonlyRootfs = true`. Plugins that write to the image FS will
fail to start. Fix patterns:

- **Write to `/tmp`** — already mounted as a 64 MiB tmpfs.
- **Bake artifacts at build time** — download models/deps in the Dockerfile, not at startup.
- **Run as non-root** — add `USER 10001` in the Dockerfile (not enforced at runtime, but the `vais plugin-init` template includes it).

### Writable workspace (opt-in)

By default the only writable path is the 64 MiB `/tmp` tmpfs — correct for short-turn,
stateless-on-disk plugins. A plugin that must keep a working tree on disk (e.g. a co-tenant
coding agent that checks out and edits a repo) can declare an opt-in workspace. It is the
**only** added writable mount; the read-only rootfs and all other Phase 1 hardening stay in place.

```yaml
spec:
  workspace:
    path: /workspace   # absolute; may not be / or /tmp. Default /workspace
    sizeMb: 4096        # required, > 0; clamped to the operator maximum
    medium: disk        # disk (default) | memory
    persist: false      # false = ephemeral; true = survives restart
```

| Field | Meaning |
|---|---|
| `path` | Mount point inside the container. |
| `sizeMb` | Size in MiB. **Hard** kernel cap for `memory`; **advisory** for `disk` (the Docker `local` driver does not enforce volume size). |
| `medium` | `disk` = a Docker named volume (survives container restart); `memory` = a tmpfs (RAM-backed, counts against the `Memory` limit). |
| `persist` | `false`: created on start, removed on stop/drain (and reset on image replace). `true`: a named volume keyed by plugin id that survives restarts, removed only when the plugin is removed. `persist: true` with `medium: memory` is rejected — a tmpfs cannot persist. |

Lifecycle is the **container's** lifetime, not per-invoke or per-session: a plugin container is
shared across concurrent invocations, so its workspace is shared too. If a coding agent needs
per-session isolation, give each session its own subdirectory under the workspace.

Operator-level caps apply: `ContainerPluginResourceBounds.MaxWorkspaceSizeMb` (default 10 GiB)
clamps oversized requests, alongside the memory/CPU/PID caps.

`medium` is an open backend identifier — `disk` and `memory` ship today; a future centralized
backend can be added without a manifest change.

**Kubernetes.** The same declaration drives `vais plugin deploy`: pass `--workspace-size-mb`,
`--workspace-path`, `--workspace-medium`, `--workspace-persist`, and `--workspace-storage-class`.
The chart renders an `emptyDir` (ephemeral; `medium: Memory` when `memory`) or a
`PersistentVolumeClaim` (`persist: true`), always preserving `readOnlyRootFilesystem: true`. Unlike
Docker, the runtime does not provision Kubernetes storage itself — the chart/operator does; the
manifest field is the single declaration that drives the rendered pod spec.

> **Note** — a `disk` workspace (Docker named volume) is exec-capable, so a coding agent can run
> build tools from it; a `memory` workspace is a tmpfs and inherits Docker's `noexec`/`nosuid`/`nodev`
> defaults (like `/tmp`), so use `disk` for build/exec workloads. Getting a repo *into* the workspace
> (clone, seed) is the agent's job, not the runtime's — the contract only provides the space.

---

## Phase 2 — Internal-network egress isolation (opt-in)

Phase 1 constrains what a plugin can do *inside* its container but does not
prevent outbound network calls. Phase 2 places the runtime and all plugin
containers on a Docker `--internal` bridge network that has no NAT gateway,
so plugins have no path to the internet by construction.

### What changes

| | Legacy (Phase 1 only) | Internal-network (Phase 2) |
|---|---|---|
| Plugin port | Published to `127.0.0.1:{port}` | Not published; addressed by container-DNS |
| Plugin URL | `http://localhost:{port}` | `http://vais-plugin-{name}:{port}` |
| Outbound internet | Allowed (via Docker bridge NAT) | Blocked (no NAT gateway on `--internal` net) |
| Runtime location | On host | In a container on the same network |
| Docker socket | Not needed | Mounted into the runtime container |

### Activate in local development

```powershell
# Create the network once (idempotent)
docker network create --internal vais-internal

# Start with the internal-network overlay
.\local-dev\dev.ps1 start -UseInternalNetwork
```

The `-UseInternalNetwork` switch:
1. Creates `vais-internal` idempotently.
2. Applies `local-dev/docker-compose.internal.yml` on top of the base compose file.
3. The runtime container joins both `vais-internal` (plugin RPC) and `vais2-network` (outbound LLM/MCP/Langfuse calls).
4. Sets `VAIS_DOCKER_PLUGIN_NETWORK=vais-internal` on the runtime.

### Activate in the demo test

```powershell
pwsh agentic/deploy/demo-test.ps1 -UseInternalNetwork
```

### Activate manually (bare `docker run`)

```powershell
docker network create --internal vais-internal

docker run -d `
  --name vais-runtime `
  --network vais-internal `
  --network vais2-network `
  -v /var/run/docker.sock:/var/run/docker.sock `
  -e VAIS_DOCKER_PLUGIN_NETWORK=vais-internal `
  -e VAIS_CONTAINER_PLUGINS_DIRECTORY=/tmp/vais-container-plugins `
  -p 8080:8080 `
  vais-research-pipeline:local
```

### Run the e2e Docker suite in internal-network mode

```powershell
pwsh agentic/tests/e2e/docker/run.ps1 -UseInternalNetwork
```

The suite creates a per-run network, starts the runtime in a container,
runs the standard test flow, and asserts that the plugin container has no
published host ports.

---

## Production hardening (daemon-wide)

These settings are applied to the Docker daemon, not the runtime. They are
deployment-operator concerns, not plugin-author concerns.

### `userns-remap` (recommended)

Maps container UID 0 to an unprivileged host UID range. Even if a plugin
escapes its container, it runs as an unprivileged host user.

```json
// /etc/docker/daemon.json
{
  "userns-remap": "default"
}
```

Restart the daemon after changing `daemon.json`. Note: `userns-remap` is
incompatible with host networking and some privileged operations.

### Rootless Docker

Run the entire Docker daemon as a non-root user (Docker 20.10+). See
[Docker rootless documentation](https://docs.docker.com/engine/security/rootless/)
for setup. Provides the strongest containment against daemon-level escapes.

### Docker socket proxy (deferred)

Instead of mounting `/var/run/docker.sock` directly into the runtime container
(which is equivalent to host root), interpose a socket proxy that exposes only
the subset of the Docker API the runtime needs:

- `POST /containers/create` — supervisor creates plugin containers
- `POST /containers/{id}/start`
- `POST /containers/{id}/stop`
- `DELETE /containers/{id}` — cleanup

A reference implementation: [tecnativa/docker-socket-proxy](https://github.com/Tecnativa/docker-socket-proxy).
Adopting it is tracked as a follow-up to Phase 2.

### `no-new-privileges` and `live-restore` as daemon defaults

```json
// /etc/docker/daemon.json
{
  "no-new-privileges": true,
  "live-restore": true
}
```

`no-new-privileges` mirrors the per-container `SecurityOpt` already applied
by Phase 1, but as a daemon-wide default for any container that doesn't opt in.
`live-restore` keeps running containers alive during a daemon restart.

---

## Platform notes

| Feature | Linux (native Docker) | Docker Desktop (macOS/Windows) |
|---|---|---|
| Phase 1 hardening | ✅ Full | ✅ Full |
| Phase 2 `--internal` network | ✅ | ✅ (WSL2 / vpnkit handle transparently) |
| DOCKER-USER iptables (Layer 3b) | ✅ Linux only | ❌ Not supported |
| `userns-remap` | ✅ | ⚠️ Supported but unusual |
| Rootless Docker | ✅ | N/A (daemon runs rootless by design) |

The `--internal` network (Phase 2) is the only cross-platform answer for
egress isolation. DOCKER-USER iptables rules are a Linux-only manual option.

---

## OTLP telemetry on the internal bridge

Even with Phase 2 egress isolation, plugins can emit OpenTelemetry spans that
land in the runtime's trace pipeline (Langfuse, Tempo, anywhere `OtlpSpanForwarder`
re-emits to). The supervisor injects `OTEL_EXPORTER_OTLP_ENDPOINT` (pointing at
the runtime's internal gateway port `5001` — `/v1/otlp/v1/traces`),
`OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`, and
`OTEL_EXPORTER_OTLP_HEADERS=Authorization=vais-plugin-token <hmac-token>`
(token TTL 24 h) into the plugin container. The Python SDK auto-configures
OpenTelemetry on `import vais_plugin` when the optional `vais-plugin[otlp]`
extra is installed; nothing else is required from the plugin author. Plugins
without the optional extra silently no-op — they never open a direct
connection to an external collector. See [container-plugins concept](../concepts/container-plugins.md#otlp-telemetry).
