# Plugin Structured-Log Endpoint

Container plugins and extensions can forward structured log records to the runtime via
`POST /v1/logs`. The runtime fans each record out to its own `ILogger` pipeline
(docker-logs, ELK/Loki, Seq, etc.) with the plugin/extension identity stamped as
structured fields. Plugin code just uses standard Python `logging` — the SDK wires
the HTTP delivery automatically.

---

## Prerequisites

`httpx` is a core dependency of `vais-plugin` (and now `vais-extension`), so no extra
install step is required. The endpoint is enabled automatically when the Docker supervisor
injects the two env vars listed below.

---

## How it works

When the runtime's Docker supervisor starts a container plugin, it injects:

| Env var | Value |
|---|---|
| `VAIS_LOG_ENDPOINT` | `http://<runtime-host>:<port>/v1/logs?source=plugin&id=<plugin-name>` |
| `VAIS_LOG_TOKEN` | HMAC-signed 24 h token for auth |

The `vais_plugin` SDK detects these at import time and installs a `VaisLogHandler` on
`logging.root`. Every subsequent `logging.info(...)` / `logging.error(...)` call is
posted to the endpoint.

For container extensions, the same mechanism applies with
`?source=extension&id=<extension-id>` in the URL. The runtime injects the same two env
vars when it starts an extension container.

---

## Wire shape

One JSON object per `POST`:

```json
{
  "timestamp": "2026-05-20T12:34:56.789Z",
  "severity":  "INFO",
  "message":   "Processed 42 records",
  "fields": {
    "component": "memory-loader",
    "count": 42
  }
}
```

| Field | Required | Notes |
|---|---|---|
| `timestamp` | No | ISO-8601; defaults to receipt time when absent |
| `severity` | No | `TRACE`, `DEBUG`, `INFO`, `WARN`, `ERROR`, `CRITICAL` (case-insensitive); defaults to `INFO` |
| `message` | Yes | Human-readable text |
| `fields` | No | Arbitrary key-value pairs; values forwarded as structured log fields |

The optional W3C `traceparent` header on the request is available for future trace
correlation (not consumed by the v1 runtime receiver, but safe to send).

---

## Runtime fan-out

The runtime forwards each record via `ILogger` with the following structured fields:

| Field | Source |
|---|---|
| `{Source}` | `plugin` or `extension` (from the query parameter) |
| `{AgentId}` | Extracted from the HMAC token (= plugin/extension name) |
| `{Extension}` | Extension id when `?source=extension` |
| `{Message}` | The message from the record |
| `{Fields}` | The entire `fields` object from the record |

These fields flow into any configured `ILogger` sinks: console, OpenTelemetry Logs
exporter, Seq, Loki, Azure Monitor, etc.

---

## Plugin example (Python)

```python
# my_plugin.py — no extra setup needed; SDK installs the handler at import time
import logging
import vais_plugin   # ← installs VaisLogHandler on logging.root automatically

log = logging.getLogger(__name__)

@vais_plugin.vais_plugin
class MyAgent(vais_plugin.PluginAgent):
    async def run(self, req):
        log.info("Starting run", extra={"run_id": req.context.run_id})
        # ... agent logic ...
        log.info("Run complete")
```

The SDK honours the standard `logging` hierarchy — set `logging.setLevel(logging.DEBUG)`
or configure a per-module logger as usual.

---

## Extension example (Python)

```python
# my_extension.py
import logging
import vais_extension   # ← installs VaisLogHandler on logging.root automatically

log = logging.getLogger(__name__)

class LogInput(vais_extension.AgentInputMiddleware):
    async def pre(self, context, call_id):
        log.debug("pre invoked for %s", context.agent_id)
        return vais_extension.PreResponse(action="next")
```

---

## Docker Compose / K8s configuration

### Docker standalone (default)

The supervisor injects `VAIS_LOG_ENDPOINT` and `VAIS_LOG_TOKEN` automatically using the
`InternalGatewayBaseUrl` (default `http://localhost:5001`). No extra configuration is
needed.

To override the base URL used for log injection (e.g. when the plugin is on a named
Docker network):

```yaml
# local-dev/runtime.env
VAIS_INTERNAL_GATEWAY_BASE_URL=http://vais-agents-runtime:5001
```

### Kubernetes

For K8s plugins, inject the env vars into the pod spec:

```yaml
env:
  - name: VAIS_LOG_ENDPOINT
    value: "http://vais-agents-runtime.vais-system.svc.cluster.local:5001/v1/logs?source=plugin&id=my-plugin"
  - name: VAIS_LOG_TOKEN
    valueFrom:
      secretKeyRef:
        name: my-plugin-log-secret
        key: log-token
```

Generate the token with:

```bash
vais token generate --plugin my-plugin --ttl 87600h
```

---

## Disabling log injection

Set `LogEndpointUrl = null` in `ContainerPluginLoaderOptions` (or leave
`VAIS_CONTAINER_PLUGINS_LOG_ENDPOINT` unset):

```csharp
services.AddContainerPlugins(opts =>
{
    opts.LogEndpointUrl = null;  // disable log injection
});
```

Or from the plugin side: unset `VAIS_LOG_ENDPOINT` before importing `vais_plugin`.

---

## Relation to OTLP spans

The structured-log endpoint is **separate** from the OTLP receiver (`POST /v1/otlp/v1/traces`).
Use OTLP spans for distributed tracing and latency attribution; use `POST /v1/logs` for
structured log records that should appear in log aggregators. Both endpoints authenticate
with the same `vais-plugin-token` scheme.

---

## See also

- `docs/deep-development/plugin-otlp-telemetry.md` — OTLP spans for plugins
- `docs/guides/observability.md` — runtime observability overview
