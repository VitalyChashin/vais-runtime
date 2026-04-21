# Reference: problem-details URNs

Every error response from the HTTP control plane carries an RFC 7807 `ProblemDetails` body with a `type` URN. The URN is a **stable contract** — status codes can overlap (a `409` covers both interrupts and idempotency conflicts), but the URN disambiguates without parsing the `detail` string.

Generated OpenAPI clients branch on URN via the `x-vais-type-urns` extension — see [consume the OpenAPI spec](../guides/consume-the-openapi-spec.md).

## Current URNs (v0.15)

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

URN structure: `urn:vais-agents:<slug>` where `<slug>` is lowercase kebab-case. No version suffix — the URN is the contract; renaming an existing URN is a **breaking change** and requires a major-version bump.

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
