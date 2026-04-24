# v0.4.0-preview — Polishing pillar + cut (§9.10 of the architectural review)

Final leg of the v0.4.0-preview work: API freeze across all packages, smoketest rewrite against the assembled 0.4 surface, `dotnet pack`, annotated tag. Companion to [`actor-agents-oss-architecture-review.md`](./actor-agents-oss-architecture-review.md) §9.10. Created 2026-04-18.

---

## Scope

1. **API freeze** — promote every package's `PublicAPI.Unshipped.txt` → `PublicAPI.Shipped.txt`. Each of the v0.4 pillar PRs accumulated Unshipped entries; this is the one-shot commit that locks them in.
2. **Smoketest rewrite** — `artifacts/smoketest/` (throwaway, gitignored) must restore + build + run against `0.4.0-preview` nupkgs and demonstrate every new pillar's surface resolves at runtime: session, memory, context providers, prompt composer, guardrails, RunBudget, tool-call dispatcher, IToolSource, termination/handoff, AgentManifest, InMemoryAgentRegistry, MCP adapter, A2A adapter.
3. **Pack** — `dotnet pack -c Release -p:VersionPrefix=0.4.0 -p:VersionSuffix=preview -o artifacts/packages`. Expect 13 `.nupkg` + 13 `.snupkg` (11 from 0.3 + Protocols.Mcp + Protocols.A2A).
4. **Tag** — annotated `v0.4.0-preview` on OSS repo `main`. Not pushed to any public feed (design-partner round governs that decision separately).

---

## Delivery — single commit

**Tasks**:

- [ ] Survey `PublicAPI.Unshipped.txt` across all 13 packages; append each non-empty file's body to the matching `PublicAPI.Shipped.txt`; reset `Unshipped.txt` to the `#nullable enable`-only stub. Build must stay green (no new warnings).
- [ ] `artifacts/smoketest/Program.cs` — rewrite to exercise every v0.4 pillar surface at runtime. No provider is wired (no API keys); type construction + `Type` round-trips through package surfaces are the target.
- [ ] `artifacts/smoketest/Directory.Packages.props` — pin every package to `0.4.0-preview`.
- [ ] Full solution build + test (`Category!=RequiresContainer`). Baseline: 287/287 before freeze; same after.
- [ ] `dotnet pack -c Release -p:VersionPrefix=0.4.0 -p:VersionSuffix=preview -o artifacts/packages` (from OSS repo root).
- [ ] Smoketest `dotnet restore` + `dotnet run`, confirming clean end-to-end against the new packages.
- [ ] `git tag -a v0.4.0-preview -m "..."` on the freeze commit. **Not pushed.**

Breaking-change ledger: Every v0.4 pillar's breaking changes (e.g. `IAiAgent.Session`, `AgentEvent` closed hierarchy, `KnowledgeRetrievalFilter` obsoleted) are already in the public surface across the 15 PRs; this freeze only promotes them. Nothing new breaks at freeze time.

---

## Progress log

- 2026-04-18 — plan created.
