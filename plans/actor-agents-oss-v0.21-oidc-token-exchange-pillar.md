# OIDC/OAuth 2.0 Token Exchange for Service-to-Service Identity (v0.21)

## Context

v0.20 ships cross-runtime graph agent refs (`GraphAgentRef.RuntimeUrl`) with simple bearer-token forwarding — the inbound `Authorization` header is passed verbatim to the remote runtime. This is zero-config and correct for same-org deployments sharing one IdP, but insufficient for production multi-org or zero-trust environments where each runtime needs audience-scoped tokens.

The deferred backlog entry (Theme 1: Identity & Security) calls for `RemoteRuntimeOptions` + configurable identity propagation, paired with an STS/issuer contract. This plan implements that as the v0.21 security-hardening pillar.

**Scope**: OIDC token exchange only. SSE streaming (`StreamAsync`) is a separate follow-up.

**STS target**: Generic RFC 8693 (no IdP-specific code). Works with Keycloak, Entra ID, Auth0, Okta, or any standards-compliant endpoint.

## Design Summary

Introduce a per-runtime configuration surface (`RemoteRuntimeOptions`) and a transport-level identity provider contract (`IRemoteIdentityProvider`) that sits inside `HttpAgentRemoteInvoker`. Three propagation modes:

1. **Forward** (default, current behavior) — pass inbound bearer token through.
2. **ServiceAccount** — use K8s projected SA token as subject token.
3. **TokenExchange** — RFC 8693 token exchange at a configured STS endpoint.

The public `IAgentRemoteInvoker.InvokeAsync` signature stays unchanged. The `bearerToken` parameter is reinterpreted from "the token to forward" to "the inbound subject token" — the invoker transforms it internally via `IRemoteIdentityProvider` before sending.

No changes required to orchestrators (`InProcessGraphOrchestrator`, `GraphNodeExecutor`, `AgentGraphLifecycleManager`) — identity propagation is a transport concern.

## New Files

| File | Purpose |
|------|---------|
| `src/Vais.Agents.Control.Http.Client/RemoteIdentityMode.cs` | Enum: `Forward`, `ServiceAccount`, `TokenExchange` |
| `src/Vais.Agents.Control.Http.Client/RemoteRuntimeOptions.cs` | Per-runtime config record with `Validate()` |
| `src/Vais.Agents.Control.Http.Client/RemoteRuntimeOptionsMap.cs` | Dictionary wrapper for DI options binding |
| `src/Vais.Agents.Control.Http.Client/IRemoteIdentityProvider.cs` | Contract: `AcquireOutboundTokenAsync(runtimeUrl, bearerToken, ct)` → `OutboundCredential` |
| `src/Vais.Agents.Control.Http.Client/ForwardingRemoteIdentityProvider.cs` | Returns inbound token unchanged |
| `src/Vais.Agents.Control.Http.Client/ServiceAccountRemoteIdentityProvider.cs` | Reads projected SA token from file, caches with TTL |
| `src/Vais.Agents.Control.Http.Client/OidcTokenExchangeRemoteIdentityProvider.cs` | RFC 8693 token exchange with STS |
| `src/Vais.Agents.Control.Http.Client/OidcTokenExchangeResponse.cs` | STS response model |
| `src/Vais.Agents.Control.Http.Client/CompositeRemoteIdentityProvider.cs` | Routes to correct provider per runtime URL |
| `tests/Vais.Agents.Control.Http.Tests/RemoteRuntimeOptionsTests.cs` | Validation tests |
| `tests/Vais.Agents.Control.Http.Tests/ForwardingRemoteIdentityProviderTests.cs` | Unit tests |
| `tests/Vais.Agents.Control.Http.Tests/ServiceAccountRemoteIdentityProviderTests.cs` | Unit tests with mock file reader |
| `tests/Vais.Agents.Control.Http.Tests/OidcTokenExchangeRemoteIdentityProviderTests.cs` | Unit tests with mock STS |
| `tests/Vais.Agents.Control.Http.Tests/CompositeRemoteIdentityProviderTests.cs` | Routing tests |

## Modified Files

| File | Change |
|------|--------|
| `src/Vais.Agents.Control.Http.Client/HttpAgentRemoteInvoker.cs` | Accept optional `IRemoteIdentityProvider`. In `InvokeAsync`, acquire outbound token via provider instead of raw forward. Accept optional `Func<string, RemoteRuntimeOptions?>` for per-runtime timeout/retry overrides. |
| `src/Vais.Agents.Control.Http.Client/HttpAgentRemoteInvokerServiceCollectionExtensions.cs` | New overload: `AddAgentRemoteInvoker(Action<RemoteRuntimeOptionsMap>?)`. Registers providers based on config. Backward-compatible: parameterless overload uses `Forward` mode. |
| `src/Vais.Agents.Runtime.Host/CompositionRoot.cs` | Wire `RemoteRuntimeOptionsMap` from config section `Vais:RemoteRuntimes`. Call `AddAgentRemoteInvoker(opts => ...)`. |
| `tests/Vais.Agents.Control.Http.Tests/HttpAgentRemoteInvokerTests.cs` | Add tests for identity-provider-driven token acquisition paths. |

## Key Interface Designs

### `IRemoteIdentityProvider`

```csharp
// src/Vais.Agents.Control.Http.Client/IRemoteIdentityProvider.cs
public interface IRemoteIdentityProvider
{
    ValueTask<OutboundCredential> AcquireOutboundTokenAsync(
        string runtimeUrl,
        string? inboundBearerToken,
        CancellationToken cancellationToken = default);
}
```

Reuses existing `OutboundCredential(string Kind, string Value, DateTimeOffset? ExpiresAt)` from `Vais.Agents.Abstractions/AgentLifecycle.cs:147`.

### `RemoteRuntimeOptions`

```csharp
// src/Vais.Agents.Control.Http.Client/RemoteRuntimeOptions.cs
public sealed record RemoteRuntimeOptions
{
    public RemoteIdentityMode IdentityMode { get; init; } = RemoteIdentityMode.Forward;

    // TokenExchange mode fields
    public Uri? TokenExchangeEndpoint { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecretRef { get; init; }  // secret:// URI
    public string? Audience { get; init; }          // defaults to runtimeUrl

    // ServiceAccount mode fields
    public string? ServiceAccountTokenPath { get; init; }  // defaults to /var/run/secrets/tokens/vais-runtime-token

    // Per-runtime transport overrides
    public TimeSpan? RequestTimeout { get; init; }
    public TimeSpan[]? RetryDelays { get; init; }

    // Cache
    public TimeSpan TokenCacheTtl { get; init; } = TimeSpan.FromMinutes(5);

    public void Validate(); // throws if required fields missing for mode
}
```

### Configuration Schema (appsettings.json)

```json
{
  "Vais": {
    "RemoteRuntimes": {
      "https://runtime-b.svc.cluster.local": {
        "IdentityMode": "TokenExchange",
        "TokenExchangeEndpoint": "https://sts.example.com/token",
        "ClientId": "vais-runtime-a",
        "ClientSecretRef": "secret://env/OIDC_CLIENT_SECRET_RUNTIME_B",
        "Audience": "vais-runtime-b",
        "RequestTimeout": "00:00:30",
        "RetryDelays": ["00:00:00.500", "00:00:01.000"]
      },
      "https://runtime-c.svc.cluster.local": {
        "IdentityMode": "ServiceAccount"
      }
    }
  }
}
```

Runtimes not listed default to `Forward` mode (v0.20 behavior preserved).

### RFC 8693 Token Exchange Request

`OidcTokenExchangeRemoteIdentityProvider` sends `POST` to `TokenExchangeEndpoint` with `application/x-www-form-urlencoded`:

```
grant_type=urn:ietf:params:oauth:grant-type:token-exchange
subject_token=<inboundBearerToken>
subject_token_type=urn:ietf:params:oauth:token-type:access_token
client_id=<ClientId>
client_secret=<resolved from ISecretResolver>
audience=<Audience ?? runtimeUrl>
```

Response (`OidcTokenExchangeResponse`):

```csharp
public sealed record OidcTokenExchangeResponse(
    string access_token,
    int expires_in,
    string token_type,
    string? issued_token_type = null,
    string? scope = null);
```

Token cached in-memory keyed by `(normalisedRuntimeUrl, audience)` with `ExpiresAt = now + expires_in - 30s` safety margin. `ConcurrentDictionary` + `SemaphoreSlim` per key prevents stampede on cache miss.

### `CompositeRemoteIdentityProvider`

Routing implementation registered as the single `IRemoteIdentityProvider` in DI:

1. Normalize `runtimeUrl`.
2. Look up in `RemoteRuntimeOptionsMap`. If found, delegate to the provider for that mode.
3. If no entry, fall back to `ForwardingRemoteIdentityProvider`.

### `HttpAgentRemoteInvoker` Changes

Constructor gains optional dependencies:

```csharp
internal HttpAgentRemoteInvoker(
    IHttpClientFactory? factory,
    IRemoteIdentityProvider? identityProvider = null,
    Func<string, RemoteRuntimeOptions?>? optionsLookup = null)
```

In `InvokeAsync`, the change is localized to `BuildRequest`:

```csharp
// Before (v0.20):
var httpRequest = BuildRequest(path, request, bearerToken);

// After (v0.21):
var outbound = _identityProvider is not null
    ? await _identityProvider.AcquireOutboundTokenAsync(normalised, bearerToken, cancellationToken)
    : new OutboundCredential("Bearer", bearerToken ?? string.Empty);
var httpRequest = BuildRequest(path, request, outbound.Value);
```

When `_identityProvider` is null, old behavior preserved — backward-compatible for tests and direct construction.

Per-runtime retry/timeout overrides: `optionsLookup(normalised)` returns options, and if `RetryDelays` is set, those override the static defaults for this invocation's retry loop. If `RequestTimeout` is set, the `HttpClient.Timeout` for that URL's keyed client is configured accordingly.

## Implementation Order

### Phase 1: Foundation (additive, non-breaking)

1. Create `RemoteIdentityMode` enum
2. Create `RemoteRuntimeOptions` record with `Validate()`
3. Create `RemoteRuntimeOptionsMap` class
4. Create `IRemoteIdentityProvider` interface
5. Implement `ForwardingRemoteIdentityProvider`
6. Unit tests: `RemoteRuntimeOptionsTests`, `ForwardingRemoteIdentityProviderTests`

### Phase 2: ServiceAccount provider

7. Implement `ServiceAccountRemoteIdentityProvider` (file read + TTL cache, mirrors `ServiceAccountTokenHandler` pattern from `src/Vais.Agents.Control.KubernetesOperator/ServiceAccountTokenHandler.cs`)
8. Unit tests with mock file reader

### Phase 3: OIDC Token Exchange provider

9. Create `OidcTokenExchangeResponse` model
10. Implement `OidcTokenExchangeRemoteIdentityProvider` (RFC 8693 HTTP POST + token cache)
11. Unit tests with mock STS (`StubHttpMessageHandler` pattern from existing tests)

### Phase 4: Composition and wiring

12. Implement `CompositeRemoteIdentityProvider`
13. Update `HttpAgentRemoteInvoker` to accept `IRemoteIdentityProvider` + options lookup
14. Update `HttpAgentRemoteInvokerServiceCollectionExtensions` with new overload
15. Update `CompositionRoot` + config binding
16. Update existing `HttpAgentRemoteInvokerTests`
17. Add `CompositeRemoteIdentityProviderTests`

### Phase 5: Config surface

18. Add commented-out `Vais:RemoteRuntimes` section to `appsettings.json`

## Existing Code to Reuse

| Component | Location | Reuse |
|-----------|----------|-------|
| `OutboundCredential` | `src/Vais.Agents.Abstractions/AgentLifecycle.cs:147` | Return type for `IRemoteIdentityProvider` |
| `ISecretResolver` | `src/Vais.Agents.Control.Abstractions/ISecretResolver.cs` | Resolve `ClientSecretRef` at runtime |
| `CompositeSecretResolver` | `src/Vais.Agents.Control.InProcess/SecretResolvers.cs:60` | Default secret resolver (env + file) |
| SA token cache pattern | `src/Vais.Agents.Control.KubernetesOperator/ServiceAccountTokenHandler.cs` | TTL + mtime-checking pattern to replicate |
| `StubHttpMessageHandler` | Test utilities pattern in existing `HttpAgentRemoteInvokerTests` | Mock STS in unit tests |
| `AddAgentRemoteInvoker()` | `HttpAgentRemoteInvokerServiceCollectionExtensions.cs` | Backward-compatible extension point |

## Verification

1. `dotnet build` — all projects compile, no new warnings
2. `dotnet test` — all new + existing tests pass
3. Manual: add a `Vais:RemoteRuntimes` section pointing at a mock STS, verify token exchange POST is correct
4. Manual: remove the config section, verify `Forward` mode is used and bearer token passes through unchanged
5. No changes to orchestrator code — `InProcessGraphOrchestrator`, `GraphNodeExecutor`, `AgentGraphLifecycleManager` untouched
