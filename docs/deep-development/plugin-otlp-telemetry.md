# Plugin OTLP Telemetry (P12 Optional Outbound)

Container plugins can emit OpenTelemetry spans that flow into the same trace as the
surrounding graph-node span. This guide explains how the feature works and how to enable it.

## How it works

The runtime injects three environment variables into every Docker plugin container when
OTLP telemetry is configured:

| Variable | Value | Purpose |
|---|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | `http://127.0.0.1:5001/v1/otlp` | Runtime's OTLP receiver |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `http/protobuf` | Binary protobuf over HTTP |
| `OTEL_EXPORTER_OTLP_HEADERS` | `Authorization=vais-plugin-token <token>` | HMAC-signed call token |
| `OTEL_RESOURCE_ATTRIBUTES` | `vais.agent_id=<plugin-name>` | Tags spans with the plugin identity |

The OTLP receiver endpoint (`POST /v1/otlp/v1/traces`) is mounted on the existing
internal gateway port (5001), so no additional port binding is needed.

When the runtime receives OTLP spans:

1. It validates the `vais-plugin-token` HMAC header.
2. It parses the protobuf body (`ExportTraceServiceRequest`).
3. It re-emits each span as a .NET `Activity` via the `Vais.Agents.Runtime.Plugins.Container.Otlp`
   `ActivitySource`, which flows into the existing OTel pipeline (OTLP exporter → Langfuse etc.).

## Enabling OTLP in Python plugins

Install the optional `otlp` extra:

```
pip install vais-plugin[otlp]
```

The `vais-plugin` SDK auto-configures the OTLP exporter on import when
`OTEL_EXPORTER_OTLP_ENDPOINT` is set. No code changes are required.

## Enabling OTLP in the runtime host

Set `OtlpEndpointUrl` in your `AddContainerPlugins` call:

```csharp
services.AddContainerPlugins(opt =>
{
    opt.OtlpEndpointUrl = "http://127.0.0.1:5001/v1/otlp";
});
```

Also map the OTLP endpoint on the internal port:

```csharp
internalApp.MapPluginOtlpEndpoints();
```

And register the ActivitySource with your TracerProvider:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddAgenticInstrumentation());
```

`AddAgenticInstrumentation()` already includes the container OTLP source name — no additional
call is needed.

## Kubernetes (Helm chart)

Enable OTLP in `values.yaml`:

```yaml
otlp:
  enabled: true
  endpoint: "http://vais-agents-runtime.vais-system.svc.cluster.local:5001/v1/otlp"
  headersSecretRef:
    name: my-plugin-otlp-token
    key: authorization
```

The Helm chart adds an egress rule to the NetworkPolicy for the internal port automatically
when `otlp.enabled` is `true`.

## Security

- The HMAC token is generated at container startup with a 24-hour TTL.
- The receiver validates the HMAC before parsing the protobuf body.
- Tokens are scoped per-plugin (the plugin name is embedded in the token payload).
- The receiver endpoint is on port 5001, which is never exposed outside the cluster.

## Tags added by the runtime

The runtime stamps every forwarded span with:

| Tag | Value |
|---|---|
| `vais.agent_id` | The plugin's registered name |
| `vais.span.source` | `plugin_otlp` |

## Limitations (v1)

- Only `string`-valued OTLP attributes are forwarded; other attribute types (int, bool, etc.)
  are silently dropped.
- The `vais.run_id` and `vais.node_id` tags are not injected automatically in v1; the plugin
  must propagate W3C `traceparent` manually for trace stitching.
