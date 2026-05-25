# Guide: deploy a stdio-only MCP server

Publish any stdio-only MCP server (the official `mcp/fetch`, `uvx mcp-server-filesystem`,
anything from [`modelcontextprotocol/servers`](https://github.com/modelcontextprotocol/servers),
your own private stdio server) against a containerised Vais.Agents runtime with one
`vais apply -f` — no runtime image rebuild, no out-of-band sidecar, no third-party
stdio↔HTTP bridge.

The runtime supervises one container per server; the container holds both the stdio MCP
child and a thin streamableHttp bridge. Gateway middleware
([`McpGatewayConfig`](../concepts/mcp-and-tool-gateway.md)), virtual server composition,
and the usual tool-resolution path apply unchanged.

## When to use this

Use `transport: containerStdio` when:

- The MCP server you want only speaks stdio (most public ones do).
- The runtime is itself containerised (the canonical deployment) and you can't bake the
  server's runtime — Python, Node, etc. — into the runtime image.
- You want one `vais apply` shape for every resource (P11) and the same isolation
  defaults as container plugins (P12).

Use one of the other transports when:

- The server already speaks `streamableHttp` or `sse` — use those directly; no
  intermediate bridge needed.
- The server's command is available on the runtime host's `$PATH` (e.g. a built-in MCP
  baked into the runtime image) — use `transport: stdio` and let
  `PhysicalMcpConnectionService` spawn the subprocess in the runtime process.

## Prerequisites

- The runtime is started with the container-supervision wiring from the
  [QUICKSTART](../../QUICKSTART.md) (Step 1): `docker.sock` mounted,
  `VAIS_CONTAINER_PLUGINS_DIRECTORY` set, `VAIS_DOCKER_PLUGIN_NETWORK` set,
  `Vais__ContainerPlugin__CallTokenSecret` set (≥32 chars).
- Docker available on the host (the CLI builds images during `vais apply`).
- The `vais` CLI pointed at your runtime (`vais config set-context …`).

## Two paths

### Path A — use the official `mcp-server-fetch` (worked example)

The reference sample [`samples/mcp-fetch-container/`](../../samples/mcp-fetch-container/)
wraps the official PyPI `mcp-server-fetch` package. Apply it directly:

```bash
vais apply -f samples/mcp-fetch-container/mcp-fetch.yaml
# → vais-mcp-mcp-fetch:1.0 built ✓
# → mcp-fetch created (mcp-server, version 1.0)
```

Within ~30 s, the container supervisor starts a hardened container on the
`vais-quickstart` network, the in-container bridge spawns `python -m mcp_server_fetch`,
and the runtime opens an MCP client over streamableHttp at the bridge port.

### Path B — bring your own stdio MCP server

Use [`samples/mcp-stdio-template/`](../../samples/mcp-stdio-template/) as a starter:

1. Copy the directory into your project.
2. Edit `Dockerfile`: add a `RUN pip install <your-stdio-mcp-package>` line (or copy a
   binary in).
3. Edit `mcp-example.yaml`: set `metadata.id`, point `spec.container.build.context` at
   the directory holding your Dockerfile, set `MCP_STDIO_CMD` to the command that starts
   your stdio MCP server.
4. `vais apply -f mcp-example.yaml`.

```yaml
apiVersion: vais.agents/v1
kind: McpServer
metadata:
  id: my-fs
  version: "1.0"
spec:
  transport: containerStdio
  mcpGatewayRef: my-mcp-gateway      # optional — bind to your gateway middleware
  container:
    build:
      context: ./mcp-stdio-template  # or wherever you copied the template
    env:
      MCP_STDIO_CMD: "uvx mcp-server-filesystem /workspace"
    resources:
      memory: 128Mi
      cpu: "0.25"
    secrets:                          # optional
      API_TOKEN: secret://env/MY_API_TOKEN
```

## Use from an agent

The agent surface is unchanged — same shape as any other MCP server.

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata: { id: researcher, version: "1.0" }
spec:
  model:
    provider: openai
    id: gpt-4o-mini
    apiKeyRef: secret://env/OPENAI_API_KEY
  systemPrompt:
    inline: |
      Call the fetch tool with a relevant URL, summarise in 2-3 sentences.
  handler: { typeName: declarative }
  protocols: [{ kind: Http }]
  llmGatewayRef: my-llm-gateway
  mcpGatewayRef: my-mcp-gateway
  mcpServers:
    - name: mcp-fetch
      transport: registered           # look up by id in the runtime registry
  tools:
    - name: fetch
      source: mcp:mcp-fetch
```

Gateway middleware (`ToolRateLimit`, `ToolOtel`, `ToolResponseTruncation`, etc.) applies
to every tool call, same as a `streamableHttp` or `stdio` server.

## Bridge env vars

The bridge reads its behaviour from env vars. Set them in `spec.container.env`.

| Var | Default | Purpose |
|---|---|---|
| `MCP_STDIO_CMD` | (required) | Full command for the stdio MCP child, e.g. `python -m mcp_server_fetch`. |
| `MCP_BRIDGE_PORT` | `7000` | Port the bridge listens on. Must match `spec.container.port`. |
| `MCP_BRIDGE_PATH` | `/mcp` | URL path. Must match `spec.container.path`. |
| `MCP_HEALTH_PATH` | `/health` | Health endpoint path. Must match `spec.container.healthPath`. |
| `MCP_SERVER_NAME` | `vais-mcp-bridge` | Identity reported on MCP `initialize`. |

The following vars are **injected automatically by the runtime** when
`Vais__ContainerPlugin__CallTokenSecret` is set (the same condition that enables OTLP for
container plugins). Do not set them manually.

| Var | Set by | Purpose |
|---|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Runtime | OTLP HTTP endpoint on the runtime's internal port (`/v1/otlp`). |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | Runtime | Always `http/protobuf`. |
| `OTEL_EXPORTER_OTLP_HEADERS` | Runtime | `Authorization: vais-plugin-token <hmac>` authenticating spans at the OTLP receiver. |
| `OTEL_RESOURCE_ATTRIBUTES` | Runtime | `vais.agent_id=<server-id>` tagging emitted spans. |
| `VAIS_LOG_ENDPOINT` | Runtime | `POST /v1/logs` endpoint URL (with `?source=plugin&id=<server-id>`). |
| `VAIS_LOG_TOKEN` | Runtime | 24-hour HMAC token for authenticating log records at the structured-log receiver. |

The stdio child process inherits all of these from the bridge process. To activate span
forwarding in Python, install `opentelemetry-sdk opentelemetry-exporter-otlp-proto-http`;
the SDK picks up the `OTEL_*` env vars automatically. For structured logs, use the
`vais-plugin-sdk` `VaisLogHandler` or post JSON directly to `$VAIS_LOG_ENDPOINT` with
`Authorization: vais-plugin-token $VAIS_LOG_TOKEN`.

## Iterate

Edit your bridge code, bump `metadata.version`, re-apply:

```bash
vais apply -f mcp-fetch.yaml
# → vais-mcp-mcp-fetch:1.1 built ✓
# → mcp-fetch updated (mcp-server, version 1.1)
```

The supervisor drains in-flight tool calls for the old container, replaces it with the
new image, polls `/health`, and resumes serving. The runtime keeps the MCP client
connection through the replace.

## Kubernetes

For K8s deployments, supply `spec.container.kubernetes` instead of (or alongside) the
build/image. The runtime patches the Deployment image on re-apply; you create the
Deployment + Service yourself (or via Helm).

```yaml
spec:
  transport: containerStdio
  container:
    image: ghcr.io/my-org/my-stdio-mcp:1.0
    kubernetes:
      serviceUrl: http://my-mcp.tools.svc.cluster.local:7000
      deploymentName: my-mcp
      namespace: tools
    env:
      MCP_STDIO_CMD: "python -m mcp_server_fetch"
```

The `KubernetesContainerSupervisor` polls `/health` on the Service URL at startup and
issues a `PATCH apps/v1/namespaces/{ns}/deployments/{name}` to bump the image on
re-apply. K8s drives the rollout.

## Production considerations

- **Image registry.** Pre-build the image in CI (`vais plugin-build` + `docker push` —
  or any standard CI step) and set `spec.container.image` to the published tag. Skip
  `build` to avoid build-on-apply in production.
- **Image-pull credentials.** Use the same secret story as container plugins
  (host-level Docker auth, or K8s `imagePullSecrets` on the Deployment).
- **Resource limits.** Container MCP servers default to 256 MiB / 0.5 CPU / 128 pids
  (same as container plugins). Most stdio MCPs need less; tune via
  `spec.container.resources` to give yourself headroom.
- **Egress control.** The supervisor attaches every container to
  `VAIS_DOCKER_PLUGIN_NETWORK`. Use `--internal` Docker network topology or K8s
  `NetworkPolicy` to constrain what the stdio MCP can reach. See
  [P12 sandbox contract](../../../research/completed/2026-05-14/plugin-isolation-contract-2026-05-13.md).
- **Observability.** Gateway middleware (`ToolOtel`) emits a span for every tool call on
  the runtime side. When `Vais__ContainerPlugin__CallTokenSecret` is set, the runtime also
  injects `OTEL_EXPORTER_OTLP_*` and `VAIS_LOG_ENDPOINT`/`VAIS_LOG_TOKEN` into the
  container so the bridge and its stdio child can export their own spans and log records to
  the runtime's receivers (see [Bridge env vars](#bridge-env-vars) above). K8s deployments
  must add `OTEL_*` to their Deployment spec directly; the K8s supervisor does not inject
  env vars.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| `vais apply` returns `400 Manifest validation failed: spec.container is required when transport is 'containerStdio'` | Either `spec.container` is missing, or your CLI predates Phase 1 of this feature. Re-pack + reinstall the CLI from source. |
| `vais apply` builds the image but the agent fails with `McpServerUnavailable` | The container supervisor scans the registry every 30 s — wait a moment after apply and re-invoke. (Tightening this is a Phase 1.5 follow-up.) |
| Container starts but never reaches Ready | The bridge isn't exposing `/health`. If you wrote your own bridge, make sure FastMCP's `custom_route("/health", methods=["GET"])` decorator is in place. |
| MCP `initialize` rejected | The upstream stdio MCP doesn't support the protocol version the .NET MCP SDK negotiates (currently `2025-11-25`). FastMCP's `as_proxy` handles version negotiation — use the template's bridge verbatim. |
| Container can't reach `host.docker.internal` from inside | You're on Linux without Docker Desktop. Use the runtime's internal-network topology (already the default with `VAIS_DOCKER_PLUGIN_NETWORK`). Don't reference `host.docker.internal` from a containerStdio container. |
| `pip install` fails with httpx conflict between `fastmcp` and `mcp-server-fetch` | Install in two RUN steps. `fastmcp` pins httpx ≥ 0.28.1; `mcp-server-fetch` pins older. Splitting lets `fastmcp` win the resolution. See [`samples/mcp-fetch-container/Dockerfile`](../../samples/mcp-fetch-container/Dockerfile). |

## See also

- [`samples/mcp-stdio-template/`](../../samples/mcp-stdio-template/) — generic template.
- [`samples/mcp-fetch-container/`](../../samples/mcp-fetch-container/) — concrete reference.
- [Concept: MCP and the tool gateway](../concepts/mcp-and-tool-gateway.md).
- [Guide: expose MCP tools to an agent](expose-mcp-tools-to-an-agent.md) — the C# library
  API path (caller manages connection).
- [Research](../../../research/mcp-stdio-native-deployment-2026-05-17.md) +
  [implementation plan](../../../plans/mcp-stdio-native-impl-2026-05-17.md).
