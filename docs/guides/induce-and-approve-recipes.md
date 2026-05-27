# Induce and approve authoring recipes (Plan D)

This guide is for operators of a Vais.Agents deployment. It walks the **descriptive↔normative loop** end-to-end: trajectories accumulate as your agents run, induction proposes candidate authoring recipes, you triage and approve them, and approved proposals land in the ontology overlay — visible to `vais.describe` and downstream consumers without a runtime restart.

The substrate this rides on is the SEP-1763 ontology-interceptor pipeline; see [concepts/ontology-substrate.md](../concepts/ontology-substrate.md) for the architecture.

## What gets recorded

Every interceptor that calls `IInterceptorTee.EmitAsync` produces a structured `TrajectoryEvent`:

| Field | Meaning |
|---|---|
| `EventId` | Stable across re-emit; primary key in the store. |
| `Timestamp` | When the interceptor fired. |
| `EventName` | Producer-defined (e.g. `tool.call`). |
| `Operation` | `Call` or `List` (the two SEP-1763 op kinds). |
| `AgentId`, `RunId`, `ConceptName`, `Transport` | Routing fingerprint — what agent, what run, what tool, north or south. |
| `ArgumentsShape` | Argument-name → type-descriptor map. **Raw values are NEVER persisted** — `TrajectoryArgumentRedactor` strips them at write time. |
| `Outcome` | `Ok` / `Error` / `ShortCircuit` plus optional error type. |
| `OntologyVersion` | Snapshot of which ontology version the call ran against. |
| `Duration` | Wall-clock span of the operation. |

The argument redactor's default deny-list (`apiKey | token | password | secret | auth | credential | privateKey | passphrase`) protects against accidental secret capture even when the corpus is shared across operators. Add deployment-specific terms with `TrajectoryArgumentRedactor.WithAdditionalSecretNameSubstrings(...)`.

## Wiring

### Trajectory store

- **In-memory (default).** A 10 000-event ring buffer; sufficient for dev and single-process pilots. No wiring required — registered as soon as the runtime starts.
- **Postgres.** Set `VAIS_INTERCEPTOR_TEE_STORE_CONNECTION`; the schema (`vais_trajectory_events`) auto-creates on first start. Retention defaults to 30 days, configurable via `RecipeProposalStoreOptions.RetentionDays` in the host-side `Configure<RecipeProposalStoreOptions>` callback.

### Proposal store

- **In-memory (default).** Backed by `ConcurrentDictionary`; suitable for single-host deployments. Status transitions are atomic via compare-and-swap; re-inducing a previously-approved proposal preserves the reviewer + decision.
- **Postgres.** Set `VAIS_RECIPE_PROPOSAL_STORE_CONNECTION`; schema `vais_recipe_proposals` auto-creates. Decided proposals (Approved / Rejected / Superseded) are pruned after 90 days by default; **Pending proposals are never auto-pruned**.

### Overlay write-back

Set `VAIS_ONTOLOGY_OVERLAY_PATH` to the on-disk overlay JSON file. When this is configured:

1. `IOntologyOverlayWriter` (the `JsonOntologyOverlayWriter`) is registered.
2. `IRecipeProposalStore` is wrapped with `OverlayPublishingRecipeProposalStoreDecorator` — approve = write + reload.
3. The active `IOntologyCatalog` becomes `HotReloadableOntologyCatalog`, which atomically swaps its inner catalog after each reload.

Without an overlay path, the loop still works for triage purposes — you can approve proposals, but they stay in the proposal store and don't land anywhere normative.

### Approval gate (Plan B integration)

`IApprovalStore` is opt-in. When present, **High-risk proposals** route through it automatically:

- Concept matching any of `delete | destroy | remove | drop | deploy | apply` (case-insensitive, configurable via `BehavioralRecipeInducerOptions.HighRiskConceptSubstrings`) ⇒ `RiskLevel.High`.
- Length-2 sequences without a high-risk match ⇒ `RiskLevel.Medium`.
- Length-3+ sequences without a high-risk match ⇒ `RiskLevel.Low`.

The hash binding the approval to its proposal is `SHA256(Kind | Concept | Body | RiskLevel | Support | Confidence)`. **Editing any of those fields invalidates a prior approval** — a re-emitted proposal with a new support count requires a fresh approval.

## The operator flow

### 1. Inspect the corpus

```sh
vais trajectories list --since 24h --limit 100
vais trajectories list --agent coord-researcher --outcome Error
vais trajectories list --concept tavily_search --transport south
```

All filters AND-combine. PII is already redacted; what you see is what's persisted.

### 2. Induce proposals

```sh
vais recipes propose                                  # over the full corpus
vais recipes propose --since 1h                       # restrict to last hour
vais recipes propose --agent coord-researcher         # restrict to one agent
```

`propose` is idempotent: running it twice produces the same proposals (and the second run does not clobber human decisions on the first).

### 3. Triage

```sh
vais recipes list                                     # newest first
vais recipes list --status Pending                    # triage queue
vais recipes list --risk High                         # high-risk first
vais recipes show <proposalId>                        # full body + traces
vais recipes show <proposalId> -o json                # for scripting
```

The `--output json` flag works on every list / show command and writes machine-readable output to stdout.

### 4. Approve or reject

```sh
vais recipes approve <proposalId>                     # by = $USER by default
vais recipes approve <proposalId> --by alice          # explicit reviewer id
vais recipes reject <proposalId> --by alice
```

For low / medium risk proposals: approve flips the status, the overlay file is updated, and the catalog rebuilds atomically — all in one CLI invocation.

For high-risk proposals, the first approve call returns:

```
Error: Approval required for high-risk Recipe '<id>'. Approval request id: req-<id>.
Run: vais approvals approve req-<id>
```

The proposal stays Pending. Run the linked `vais approvals approve` command — this is the same surface used for `ContainerPlugin` / `Extension` / `Plugin` mutations, so the same audit trail and operator scope apply. Then re-run the recipe approve; the gate sees the matching `IApprovalStore` approval and flips the proposal.

### 5. Confirm the loop closed

```sh
vais describe <kind>                                  # for a TagSuggestion / DescriptionRewrite
cat $VAIS_ONTOLOGY_OVERLAY_PATH                       # raw overlay JSON
vais recipes show <proposalId>                        # status now Approved
```

`vais.describe` reads from the live `IOntologyCatalog` — which the reloader just swapped. No restart needed.

## What can go wrong

- **"No proposals emitted."** The corpus didn't yield any pattern with `support ≥ MinSupport` (default 3). Either drive more workload, lower the threshold (host-side: register `BehavioralRecipeInducerOptions { MinSupport = 2 }`), or restrict `--since` to a window with more activity.
- **"Approval required for high-risk Recipe…"** — expected for any proposal flagged High-risk by the concept deny-list. Approve the underlying `IApprovalStore` request and retry.
- **Overlay file change not visible in `vais.describe`** — confirm `VAIS_ONTOLOGY_OVERLAY_PATH` is set in your runtime config; without it, the catalog stays as the embedded base + no reload happens. Also confirm `HotReloadableOntologyCatalog` is registered (it is, automatically, when the overlay path is configured).
- **"Decision durable despite side-effect failure."** If the overlay file is unwritable (permissions, path missing), the proposal still flips to Approved in the store — the side effect logs an error but does not roll back the decision. Set `OverlayPublishingRecipeProposalStoreDecorator(..., throwOnSideEffectFailure: true)` for strict pipelines that must roll back.

## What this loop does *not* do

- **No automatic application.** Induction proposes; humans dispose. There is no "approve all", no "auto-approve below threshold", no scheduled write-back. Every change to the normative ontology has a named reviewer.
- **No multi-tenant overlay.** A single deployment owns a single overlay file. Multi-tenant ontology storage is out of scope for Plan D.
- **No corpus replication across deployments.** Trajectories stay deployment-local by design — the data is sensitive (argument shapes can leak workload patterns) and the corpus reflects this deployment's actual usage, not a shared norm.

## Producer-side coverage as of Plan D

The trace middleware `DomainOntologyTraceMiddleware` is auto-wired into the south cartridge whenever an `IInterceptorTee` is registered AND the agent's tools reference an `McpServer` with `OntologyRef`. **Native C# agents and container-plugin agents (Python, Go) emit trajectories identically** — the container-gateway endpoint (`POST /v1/container-gateway/tools/invoke`) resolves the calling agent's per-agent `ToolGatewayMiddleware` chain through `IAgentManifestTranslator.ResolvePerAgentChainsAsync`, so the south cartridge fires on every plugin tool call exactly as it does for in-process calls.

For the in-process correctness of the propose → approve → overlay-write → catalog-reload chain, see `OntologyCatalogReloaderTests.Decorator_ApproveTriggersOverlayWriteAndCatalogReload` — that integration test stands up the full chain against a real overlay file and the hot-reloadable catalog.

## Sequence — at a glance

```
┌──────────┐                                        ┌─────────────────────────┐
│  agent   │──InterceptorTeeEvent──▶ IInterceptorTee│ RecordingInterceptorTee │
└──────────┘                                        └────────────┬────────────┘
                                                                 │ TrajectoryEvent (PII-redacted)
                                                                 ▼
                                            ┌──────────────────────────────────┐
                                            │  IInterceptorTeeStore             │
                                            │  (in-memory | Postgres)           │
                                            └────────────┬─────────────────────┘
                                                         │
                                          vais recipes propose │
                                                         ▼
                                            ┌──────────────────────────────────┐
                                            │  IRecipeInducer                   │
                                            │  Behavioral + LlmAssisted         │
                                            └────────────┬─────────────────────┘
                                                         │ RecipeProposal
                                                         ▼
                                            ┌──────────────────────────────────┐
                                            │  IRecipeProposalStore             │
                                            │  (Pending → Approved | Rejected)  │
                                            └────────────┬─────────────────────┘
                                                         │
                              vais recipes approve <id>  │
                                                         ▼
                            ┌─────── if High-risk ───────┴──────────────┐
                            │                                            │
                            ▼                                            ▼
              ┌────────────────────────┐                ┌────────────────────────┐
              │  IApprovalStore         │                │  Decorator side-effects │
              │  (Plan B gate)          │                │  IOntologyOverlayWriter │
              │  vais approvals approve │  match hash    │  + reload signal        │
              └────────────┬───────────┘                └────────────┬───────────┘
                           │                                          │
                           └────────────────────┬─────────────────────┘
                                                ▼
                                  ┌──────────────────────────────────┐
                                  │  Overlay JSON updated atomically  │
                                  │  + HotReloadableOntologyCatalog    │
                                  │    swaps inner catalog            │
                                  └────────────┬─────────────────────┘
                                               ▼
                                       vais describe <kind>
                                       (reflects approved change)
```
