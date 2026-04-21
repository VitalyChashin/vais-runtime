# Guide: consume the OpenAPI spec

The HTTP control plane publishes a machine-readable OpenAPI 3.1 document at `GET /openapi/v1.json` (or whatever document name you pick). Every error response carries an `x-vais-type-urns` extension — the same URN strings the server returns in `ProblemDetails.type` — so generated clients can branch on the URN instead of parsing free-text messages.

Shipped in v0.11 with `AddAgentControlPlaneOpenApi` + `MapAgentControlPlaneOpenApi` + `VaisProblemDetailsOperationTransformer` (in `Vais.Agents.Control.Http.Server`), built on `Microsoft.AspNetCore.OpenApi 9.0.11`.

## Enable the spec

```csharp
using Vais.Agents.Control.Http;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAgentControlPlane();
builder.Services.AddAgentControlPlaneOpenApi();              // default document name "v1"

var app = builder.Build();
app.MapAgentControlPlane();
app.MapAgentControlPlaneOpenApi();                           // /openapi/v1.json

app.Run();
```

Fetch it:

```bash
curl http://localhost:5000/openapi/v1.json | jq
```

Pick a different document name when you need to serve multiple specs side-by-side (e.g. `v1` for current + `v2-preview` during a cutover):

```csharp
builder.Services.AddAgentControlPlaneOpenApi(documentName: "v2-preview");
app.MapAgentControlPlaneOpenApi();     // scans registered documents — v1 + v2-preview both served
```

## What the spec contains

- Every endpoint mapped by `MapAgentControlPlane` with its full request/response JSON schema (registered via AspNetCore's `Microsoft.AspNetCore.OpenApi`).
- `ProblemDetails` response bodies on `400` / `403` / `404` / `409` / `422` / `429` / `501` / `503` as per server contract.
- `Idempotency-Key` header declared as an optional parameter on mutating verbs.
- `x-vais-type-urns` extension on every error response — an array of URN strings the server may return with that status code.

## Reading `x-vais-type-urns`

Each error response in the spec carries an `x-vais-type-urns` extension. For example, a `409 Conflict` on `POST /v1/agents/{id}/invoke` looks like:

```jsonc
"responses": {
  "409": {
    "description": "Conflict",
    "content": {
      "application/problem+json": {
        "schema": { "$ref": "#/components/schemas/ProblemDetails" }
      }
    },
    "x-vais-type-urns": [
      "urn:vais-agents:interrupt-pending",
      "urn:vais-agents:idempotency-in-flight"
    ]
  }
}
```

Generated client code can branch on the URN:

```csharp
try
{
    await client.InvokeAsync(…);
}
catch (ApiException ex) when (ex.StatusCode == 409)
{
    switch (ex.ProblemDetails?.Type)
    {
        case "urn:vais-agents:interrupt-pending":
            // A prior run is awaiting human signal. Fetch the interrupt + surface it.
            break;
        case "urn:vais-agents:idempotency-in-flight":
            // Retry after `Retry-After` seconds with the same key.
            break;
    }
}
```

The URN is a **stable contract**. Status codes can overlap (`409` serves both interrupts and idempotency conflicts); the URN disambiguates without parsing the message string. See the [problem-details URNs reference](../reference/problem-details-urns.md) for the full table.

## Code generation

Three commonly-used generators round-trip cleanly with the spec. All three preserve the `x-vais-type-urns` extension so downstream consumers can branch on URN rather than status code alone.

### NSwag (C# client)

```bash
dotnet tool install -g NSwag.ConsoleCore
nswag openapi2csclient /input:http://localhost:5000/openapi/v1.json /namespace:MyApp.Vais /output:Generated/VaisClient.cs
```

NSwag surfaces vendor extensions on the generated `ApiException` payload — consumers can read `ex.Headers["x-vais-type-urns"]` or access the extension through `ex.ProblemDetails.Extensions`.

### Kiota (multi-language)

```bash
dotnet tool install -g Microsoft.OpenApi.Kiota
kiota generate --openapi http://localhost:5000/openapi/v1.json --language CSharp --class-name VaisClient --namespace-name MyApp.Vais --output Generated/
```

Kiota keeps the URN list attached to the generated error model. For Go / TypeScript / Python clients, swap the `--language` flag.

### openapi-typescript (TypeScript)

```bash
npx openapi-typescript http://localhost:5000/openapi/v1.json -o src/vais-api.d.ts
```

The generated types preserve vendor extensions as typed properties on the error schemas:

```typescript
import type { components } from "./vais-api";
type InvokeErrorUrns = components["responses"]["InvokeAgent_409"]["x-vais-type-urns"];
//   ^? readonly ["urn:vais-agents:interrupt-pending", "urn:vais-agents:idempotency-in-flight"]
```

Pair with a runtime fetch wrapper that reads `ProblemDetails.type` and narrows to the union.

## Regenerating on every build

The spec is generated at runtime from reflection over the route handlers, so it's always in sync with the code — no separate schema file to drift. Two common CI patterns:

1. **Snapshot test** — add a test that asserts the live spec matches a checked-in JSON golden file. `dotnet test` catches unintended surface changes.
2. **Publish as a build artifact** — run a one-shot startup that `curl`s the spec + commits it to a `schemas/` folder. Downstream teams pull from there instead of spinning up the server.

Either way, the spec itself is cheap to fetch — under 100 KB in the current control-plane surface.

## What the `VaisProblemDetailsOperationTransformer` actually does

Internally, `AddAgentControlPlaneOpenApi` registers a `VaisProblemDetailsOperationTransformer` that walks every operation, reads the status-code-to-URN map from `ProblemDetailsMapping`, and attaches the `x-vais-type-urns` array to the matching response entry. One place to update — edits to `ProblemDetailsMapping` propagate automatically.

If you need to add your own URNs alongside the built-ins (e.g. a host-specific `urn:acme:tenant-suspended`), register a second operation transformer via the standard AspNetCore hook:

```csharp
builder.Services.AddAgentControlPlaneOpenApi();
builder.Services.AddOpenApi("v1", options =>
{
    options.AddOperationTransformer((operation, context, ct) =>
    {
        // Append your URNs to existing x-vais-type-urns arrays on targeted operations.
        return Task.CompletedTask;
    });
});
```

## Limitations

- **Spec is regenerated on every fetch.** Heavy in aggregate but per-fetch is fast (under 20 ms on the current surface). Add a cache in front if you hit the route thousands of times per second — most callers fetch it once at startup and hand the JSON around.
- **SSE streaming routes appear in the spec but the response type is `text/event-stream`** — code generators typically fall back to raw `Stream`. Use the client's `InvokeStreamEventsAsync` method instead of a generated binding for these routes.
- **No Swagger UI shipped in the box.** `Microsoft.AspNetCore.OpenApi` emits the JSON; pair with `Swashbuckle.AspNetCore.SwaggerUI` or `Scalar.AspNetCore` if you want the rendered docs page.

## See also

- [Control plane concept](../concepts/control-plane.md) — the verb set the spec covers.
- [Enable HTTP idempotency](enable-http-idempotency.md) — the `Idempotency-Key` header the spec advertises.
- [Problem-details URNs reference](../reference/problem-details-urns.md) — the authoritative URN list.
- `samples/OpenApiGeneration` — runnable walkthrough (pending — see [samples plan](../../plans/actor-agents-oss-housekeeping-samples-plan.md)).
