# v0.4.0-preview — Control-plane pillar (§9.8 of the architectural review)

Tactical plan for the eighth pillar — the "Kubernetes for agents" surface. Companion to [`actor-agents-oss-architecture-review.md`](./actor-agents-oss-architecture-review.md) §9.8 (and §5). Created 2026-04-18.

---

## Scope

Ship the control-plane **contracts** — declarative manifest shape + lifecycle verb interface + registry + identity provider — without committing to a cloud-runtime engine yet. The value right now is: design partners can start writing manifests, consumers can build their own lifecycle backends, and the Orleans-backed reference implementation lands in Phase 3 of the main research doc.

**What ships**:

1. **Data records**: `AgentManifest` + sub-records (`AgentHandlerRef`, `ProtocolBinding`, `ToolRef`, `MemoryRef`, `IdentityRef`, `AutoscalingSpec`), plus the invocation/signal/status/principal/credential records.
2. **Interfaces**: `IAgentLifecycleManager`, `IAgentRegistry`, `IAgentIdentityProvider`.
3. **One concrete Core impl**: `InMemoryAgentRegistry` — proves the registry shape works and gives consumers a usable default. No lifecycle-manager impl; that's cloud-runtime work.

**Explicitly deferred** (confirmed from the review's §5.3 non-goals list):

- No HTTP control-plane API (lifecycle manager is the in-process contract; HTTP adapter lands later).
- No CRD / YAML dialect.
- No policy engine, quotas, ABAC.
- No multi-region / federation.
- No `IAgentLifecycleManager` default impl — that's the cloud runtime's job (Phase 3).
- No `IAgentIdentityProvider` default impl — that's security-engine territory.

**Design decisions settled 2026-04-18**:

1. **All types in the `Vais2.Agents` namespace** (flat, consistent with existing `IAgentRuntime`, `IAgentRegistry` would fit naturally alongside). No `Vais2.Agents.ControlPlane` sub-namespace.
2. **Ship lifecycle-manager interface without implementation.** Unlike the §9.7 decision to skip `IAgentGraphExecutor` (too design-speculative), the lifecycle verbs (Create/Invoke/Signal/Query/Cancel/Update/Evict) are a well-surveyed universal primitive — AgentCore, Temporal, Restate, Dapr all converge on this verb set. Consumers wiring custom control planes benefit from a stable contract now.
3. **`AgentSignal(Kind, Payload)`** stays minimal — `Kind` is a consumer-chosen string tag (e.g., `"resume"`, `"cancel-pending"`, `"reload-config"`). No enum — keeps the surface open for consumer-defined signals without API churn.
4. **`AgentStatus` enum**: `Unknown / Active / Idle / Paused / Terminated`. Five states cover the universal states from the prior-art survey (AgentCore's endpoint states, Temporal's workflow status, OpenAI Assistants' run statuses). Additional domain-specific states land as consumer extensions.
5. **`AgentHandle(AgentId, Version, InstanceId?)`** — three-field tuple. `InstanceId` is nullable because not every lifecycle impl needs it (in-memory registries key by `AgentId + Version`; session-grain-style runtimes keyed by instance need the third slot).
6. **`InMemoryAgentRegistry`** supports the `ListAsync(label?)` + `GetAsync(id, version?)` surface. Label filter is simple string-prefix match. No event-based notifications (deferred).

---

## Delivery — single PR

**Packages**: `Vais2.Agents.Abstractions`, `Vais2.Agents.Core`.

Tasks:

- [x] Abstractions (data records): `AgentManifest`, `AgentHandlerRef`, `ProtocolBinding`, `ToolRef`, `MemoryRef`, `IdentityRef`, `AutoscalingSpec`, `AgentHandle`, `AgentInvocationRequest`, `AgentInvocationResult`, `AgentSignal`, `AgentStatus` (enum), `AgentPrincipal`, `OutboundCredential` — 14 types in `Vais2.Agents` namespace.
- [x] Abstractions (interfaces): `IAgentLifecycleManager` (7 verbs: Create / Invoke / Signal / Query / Cancel / Update / Evict), `IAgentRegistry` (List + Get), `IAgentIdentityProvider` (authenticate inbound + acquire outbound).
- [x] Core: `InMemoryAgentRegistry` — concurrent dict keyed by `(id, version)`, `Register` / `Remove` helpers. `ListAsync` with label-prefix-match filter. `GetAsync` with null-version → latest-lexicographically.
- [x] Tests — 12 new: 5 `AgentManifestRecordTests` + 7 `InMemoryAgentRegistryTests`. 259/259 non-container green.
- [x] `PublicAPI.Unshipped.txt` updates — 230 new entries in Abstractions (record auto-members + enum + interfaces), 5 in Core.

Breaking-change ledger: None. Pure additions.

---

## Progress log

- 2026-04-18 — plan created. Six design decisions settled (flat namespace, ship lifecycle interface without impl, minimal `AgentSignal` shape, five-state status enum, three-field handle, in-memory registry with prefix label filter).
- 2026-04-18 — PR complete on local working tree. 14 data records + 3 interfaces in Abstractions; `InMemoryAgentRegistry` impl in Core. 12 new tests, 259/259 non-container green, 0 warnings.
