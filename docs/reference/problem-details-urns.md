# Reference: problem-details URNs

Every error response from the HTTP control plane carries an RFC 7807 `ProblemDetails` body with a `type` URN. The URN is a **stable contract** — status codes can overlap (a `409` covers both interrupts and idempotency conflicts), but the URN disambiguates without parsing the `detail` string.

Generated OpenAPI clients branch on URN via the `x-vais-type-urns` extension — see [consume the OpenAPI spec](../guides/consume-the-openapi-spec.md).

## Current URNs

| URN | Pairs with | Meaning | Ships in | Typical caller response |
|---|---|---|---|---|
| `urn:vais-agents:bad-request` | `400` | Malformed request — body parse error, header validation failure, `Idempotency-Key` too long. | v0.6 | Fix the payload; retry. |
| `urn:vais-agents:manifest-invalid` | `400` | Agent manifest failed validation — invalid shape, missing required fields, circular handoff, cycle without `maxSteps`. | v0.6 | Fix the manifest per `detail`; retry. |
| `urn:vais-agents:policy-denied` | `403` | `IAgentPolicyEngine` returned `Deny`. | v0.6 (contract) / v0.14 (OPA impl) | Obtain authorisation out-of-band; retry is pointless otherwise. |
| `urn:vais-agents:agent-not-found` | `404` | No agent with the given `{id}` (and optional `{version}`) in the registry. | v0.11 | Check spelling; list agents via `GET /v1/agents`. |
| `urn:vais-agents:interrupt-pending` | `409` | A prior run is awaiting a signal — cannot start a fresh invoke on the same agent+session. | v0.11 | Fetch the interrupt state; deliver the signal via `POST /v1/agents/{id}/signal` first. |
| `urn:vais-agents:idempotency-in-flight` | `409` | Same `Idempotency-Key` is currently in use by an unfinished request. Response includes `Retry-After: N`. | v0.11 | Wait `Retry-After` seconds; retry with the same key. |
| `urn:vais-agents:idempotency-mismatch` | `422` | Same `Idempotency-Key` was previously bound to a request with a different SHA-256 body fingerprint. | v0.11 | Reuse the original body, or mint a new key. |
| `urn:vais-agents:budget-exceeded` | `429` | `RunBudget` tripped mid-run (`MaxTurns`, `MaxPromptTokens`, `MaxCompletionTokens`, `MaxToolCalls`, or `MaxDuration`). | v0.11 | Inspect `detail` for the field name; adjust budget or accept the result. |
| `urn:vais-agents:streaming-not-supported` | `501` | Resolved agent does not implement `IStreamingAiAgent`. | v0.12 | Use `POST /v1/agents/{id}/invoke` (unary); or swap in a streaming-capable agent. |
| `urn:vais-agents:backend-unavailable` | `503` | Upstream dependency (Orleans silo, DB, vector store) unreachable. | v0.6 | Retry with exponential backoff; escalate on persistent failure. |

### Manifest instantiation (v0.17 Pillar B)

Emitted by `AgentManifestTranslator` during grain activation — surfacing as HTTP responses on the first invoke that follows `vais apply`.

| URN | Pairs with | Meaning | Ships in | Typical caller response |
|---|---|---|---|---|
| `urn:vais-agents:handler-not-loaded` | `501` | Manifest has no `Model` and no loaded plugin exports the requested `Handler.TypeName`. | v0.17 | Ship a plugin that exports the handler, or add a `Model` block to the manifest. |
| `urn:vais-agents:model-provider-unsupported` | `400` | `ModelSpec.Provider` doesn't match any registered `IModelProviderFactory`. | v0.17 | Register a matching factory (see ship-a-custom-model-provider guide), or use one of `openai` / `anthropic` / `azure-openai`. |
| `urn:vais-agents:prompt-spec-ambiguous` | `400` | `SystemPromptSpec` has more than one of `inline` / `templateRef` / `fileRef` set. | v0.17 | Pick exactly one shape. |
| `urn:vais-agents:prompt-template-not-registered` | `400` | `templateRef` doesn't resolve in the `IPromptTemplateRegistry`. | v0.17 | Register the template at host startup or fix the ref name. |
| `urn:vais-agents:prompt-file-unreadable` | `400` | `fileRef` missing / permissioned / outside the configured root. | v0.17 | Mount the file + adjust the root path. |
| `urn:vais-agents:tool-not-registered` | `400` | `static:<name>` has no matching `IStaticToolRegistry` entry. | v0.17 | Register the tool at host startup or fix the ref name. |
| `urn:vais-agents:tool-source-unknown` | `400` | `ToolRef.Source` prefix is not `static:` / `mcp:` / `a2a:`. | v0.17 | Use a supported prefix. |
| `urn:vais-agents:mcp-server-not-declared` | `400` | `mcp:<name>` references an undeclared `McpServers[].Name`. | v0.17 | Add the server declaration to the manifest. |
| `urn:vais-agents:mcp-server-unavailable` | `503` | `transport: plugin` server has no live `IToolSource` under that name — plugin not loaded or still starting. | v0.23 | Confirm the plugin is listed as Ready (`vais plugins list`); retry after startup. |
| `urn:vais-agents:mcp-tool-not-found` | `400` | A tool declared with `source: mcp:<name>` was not found in the server's `tools/list` response. | v0.23 | Verify the tool name matches `[tool.vais.plugin].tools` and the server's `@mcp.tool()` decorator. |
| `urn:vais-agents:a2a-agent-not-declared` | `400` | `a2a:<name>` references an undeclared `A2ARemoteAgents[].Name`. | v0.17 | Add the remote-agent declaration to the manifest. |
| `urn:vais-agents:guardrail-not-registered` | `400` | `GuardrailRef.Name` has no matching `IGuardrailFactory` for the layer. | v0.17 | Register a factory or pick a built-in (`LengthCap` / `RegexAllowlist` / `RegexDenylist` / `LlmAsJudge`). |
| `urn:vais-agents:guardrail-params-invalid` | `400` | Factory rejected the supplied `params` (missing key, wrong type, bad value). | v0.17 | Fix the params per the factory's documented schema. |

### Plugin model (v0.18 Pillar C)

Two runtime URNs reach the HTTP wire; four loader URNs are startup-log-only (WARN level, loader continues). See [runtime-plugins concept](../concepts/runtime-plugins.md#urn-catalogue) for the full loader catalogue.

| URN | Pairs with | Meaning | Ships in | Typical caller response |
|---|---|---|---|---|
| `urn:vais-agents:plugin-factory-throw` | `500` | `IAgentHandlerFactory.CreateAsync` threw during grain activation. Inner exception message surfaces via `detail`. Factory throws do NOT cache a partial result — a retry re-invokes the factory. `OperationCanceledException` propagates unwrapped. | v0.18 | Inspect `detail`; fix the factory. Transient failures clear on the next invoke. |
| `urn:vais-agents:handler-and-declarative-fields-both-set` | — | Apply-time WARN on the `vais apply` response body. Manifest has both a loaded-plugin `Handler.TypeName` AND declarative `Model` fields. Plugin wins; declarative fields are silently ignored. | v0.18 | Remove `Model` / `SystemPromptSpec` / `Tools` / `GuardrailsSpec` from the manifest, or retype `handler.typeName` to the `declarative` sentinel. |
| `urn:vais-agents:plugin-load-failed` | — | **Startup log only.** A plugin folder's primary DLL failed `Assembly.LoadFromAssemblyPath`. Loader continues with the next plugin. | v0.18 | Inspect logs; republish the plugin with its transitive deps. |
| `urn:vais-agents:plugin-abi-mismatch` | — | **Startup log only.** Plugin `targetApiVersion` doesn't match the runtime ABI. Plugin skipped. | v0.18 | Rebuild the plugin against the runtime's Abstractions version. |
| `urn:vais-agents:plugin-handler-collision` | — | **Startup log or throw.** Two plugins declared the same `HandlerTypeName`. Throws on `PluginLoaderOptions.FailOnHandlerCollision=true` (default); otherwise first-registered wins. | v0.18 | Rename one plugin's handler or dedicate a namespace. |
| `urn:vais-agents:plugin-handler-not-found` | — | **Startup log only.** A `[VaisPlugin(..., "Foo")]` declared `"Foo"` but the loaded assembly has no matching type. Handler skipped. | v0.18 | Fix the attribute or type name mismatch. |

### Python plugin (v0.23 Pillar E)

Emitted by `IPythonPluginHost` at silo startup — startup-log-only unless `transport: plugin` surfaces an unavailable server through the translator (which produces an HTTP error).

| URN | Pairs with | Meaning | Ships in | Typical caller response |
|---|---|---|---|---|
| `urn:vais-agents:python-plugin-load-failed` | — | **Startup log only.** Descriptor parse error or missing file. Loader continues with the next plugin. | v0.23 | Inspect the startup log; fix `plugin.yaml` or `pyproject.toml`. |
| `urn:vais-agents:python-plugin-abi-mismatch` | — | **Startup log only.** `[tool.vais.plugin].targetApiVersion` doesn't match the runtime ABI (`0.23`). Plugin skipped. | v0.23 | Update `targetApiVersion = "0.23"` in `pyproject.toml`. |
| `urn:vais-agents:python-plugin-handshake-timeout` | — | **Startup log only.** MCP `initialize` handshake did not complete within the budget. Subprocess killed; plugin skipped. | v0.23 | Run the server manually to diagnose; increase `handshakeTimeoutSeconds` if slow-starting. |
| `urn:vais-agents:python-plugin-exited` | — | **Startup log / restart loop.** Subprocess exited unexpectedly. With `restartPolicy: exponentialBackoff`, the supervisor retries. | v0.23 | Check subprocess stderr in the runtime container logs. |
| `urn:vais-agents:python-plugin-unavailable` | `503` (via translator) | All restart attempts exhausted. Tool calls for this plugin fail until the subprocess recovers. Also surfaced as HTTP `503` when the translator resolves a `transport: plugin` server. | v0.23 | Restart the runtime or fix the plugin; the supervisor will retry after the next silo restart. |
| `urn:vais-agents:python-plugin-ambiguous-folder` | — | **Startup log only.** Multiple `plugin.yaml` files detected in a single plugin subfolder. Plugin skipped. | v0.23 | Keep exactly one `plugin.yaml` per plugin directory. |

URN structure: `urn:vais-agents:<slug>` where `<slug>` is lowercase kebab-case. No version suffix — the URN is the contract; renaming an existing URN is a **breaking change** and requires a major-version bump.

### Python agent (v0.24 Pillar F)

Emitted by `PythonSubprocessSupervisor` and `PythonAgentShim` during agent invocation. Unlike v0.23 plugin URNs (startup-log-only), these surface as exceptions to the grain caller and may propagate to HTTP clients as `500` errors.

| URN | Pairs with | Meaning | Ships in | Typical caller response |
|---|---|---|---|---|
| `urn:vais-agents:python-agent-invoke-failed` | `500` | The Python `invoke` coroutine threw an unhandled exception, or the subprocess returned a JSON-RPC error response. State is unchanged. | v0.24 | Check Python subprocess stderr in runtime container logs; fix user code. |
| `urn:vais-agents:python-agent-invoke-timeout` | `504` | `vais/agent.invoke` did not complete within `invokeTimeoutSeconds`. The subprocess is **not** killed; it remains Ready for the next call. State is unchanged. | v0.24 | Increase `invokeTimeoutSeconds` in `plugin.yaml`; optimise the agent loop. |
| `urn:vais-agents:python-agent-state-too-large` | `500` | The `newState` blob returned by the subprocess exceeds `MaxAgentStateSizeBytes` (default 1 MiB). The previous state is preserved; this turn's state is discarded. | v0.24 | Trim the state payload; raise `VAIS_PYTHON_AGENT_MAX_STATE_BYTES`; or cap LangGraph history on the Python side. |
| `urn:vais-agents:python-agent-protocol-error` | `500` | The subprocess returned a response that could not be deserialised as `AgentInvokeResponse` (malformed JSON, missing required fields). State is unchanged. | v0.24 | Ensure the server correctly serialises `assistantMessage`; check `vais-agent-sdk` version matches `targetApiVersion = "0.24"`. |
| `urn:vais-agents:python-agent-handler-collision` | — | **Startup log only.** A Python `handler.typeName` conflicts with an already-registered .NET or Python plugin handler. Both are refused; neither loads. | v0.24 | Ensure `handler.typeName` is globally unique across all plugins in the plugins directory. |

## Usage from a typed client

```csharp
try
{
    var reply = await client.InvokeAsync(agentId, request, version: null, idempotencyKey);
}
catch (AgentControlPlaneException ex)
{
    switch (ex.Type)
    {
        case "urn:vais-agents:interrupt-pending":
            // Block until the operator delivers the signal; or drop out and notify.
            var interrupt = await client.GetInterruptAsync(agentId);
            await NotifyApproverAsync(interrupt);
            break;

        case "urn:vais-agents:idempotency-in-flight":
            var retryAfter = ex.RetryAfter ?? TimeSpan.FromSeconds(5);
            await Task.Delay(retryAfter);
            // Retry with the same key.
            break;

        case "urn:vais-agents:budget-exceeded":
            Log.Warning("Run hit {Field} budget: {Detail}", ex.Extensions["field"], ex.Detail);
            break;

        case "urn:vais-agents:policy-denied":
            throw new UnauthorizedAccessException(ex.Detail);

        default:
            throw;
    }
}
```

`AgentControlPlaneException` surfaces the URN as `ex.Type`; use `ex.Status` for the status code (may be unreliable if an intermediate proxy rewrites), `ex.Detail` for the human-readable explanation, and `ex.Extensions` for any URN-specific fields (`field` for `budget-exceeded`, `expected-fingerprint` for `idempotency-mismatch`, etc.).

## Adding host-specific URNs

The control-plane server ships the URNs above; hosts that need additional error shapes declare their own URNs under their own namespace — never reuse the `urn:vais-agents:` prefix for host-specific errors:

```csharp
builder.Services.AddOpenApi("v1", options =>
{
    options.AddOperationTransformer((operation, context, ct) =>
    {
        // Append `urn:acme:tenant-suspended` to selected 403 responses.
        return Task.CompletedTask;
    });
});
```

Hosts return their own URNs by constructing `ProblemDetails` directly in a minimal-API handler or controller action:

```csharp
app.MapPost("/v1/agents/{id}/invoke", async (string id, HttpContext http) =>
{
    if (await IsTenantSuspendedAsync(http.User))
    {
        return Results.Problem(
            type: "urn:acme:tenant-suspended",
            title: "Tenant suspended",
            detail: "Your tenant is in read-only mode pending payment.",
            statusCode: 403);
    }
    // ...
});
```

## See also

- [Consume the OpenAPI spec](../guides/consume-the-openapi-spec.md) — how URNs surface as `x-vais-type-urns`.
- [Enable HTTP idempotency](../guides/enable-http-idempotency.md) — the four idempotency URNs in context.
- [Control plane concept](../concepts/control-plane.md) — where error paths sit across the verb set.
- `ProblemDetailsMapping` in `Vais.Agents.Control.Http.Server` — the authoritative source of the status-code-to-URN map.
