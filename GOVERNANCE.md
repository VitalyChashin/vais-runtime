# Governance

Vais.Agents is in Phase 1 (pre-alpha). During Phase 1, governance is a **benevolent-dictator** model: maintainers drive the roadmap, accept or reject PRs on scope, and tag releases. Decisions are made in the open via issues, PRs, and inline commit/PR descriptions — contributors are free to disagree and propose alternatives, but the final call rests with the maintainers.

## Roles

- **Maintainer.** Has commit access, can merge PRs, can tag releases. The current maintainer set is recorded in [`.github/CODEOWNERS`](.github/CODEOWNERS).
- **Contributor.** Anyone whose PR has been merged. Contributors do not have commit access; their changes land via reviewed PRs.

## How decisions get made

| Change type | Path |
|---|---|
| Bug fixes; small docs / sample improvements | One maintainer approval, then merge. |
| New features; behavioural changes; API additions | Open an issue first. A maintainer signs off on the design before significant implementation work begins. |
| Breaking changes | Migration guidance required in the PR and `CHANGELOG.md` under `Changed`. Maintainer sign-off required. |
| Architecture decisions | Captured inline in the PR description and the commit body; substantive rationale also lands in `CHANGELOG.md` and the relevant concept page. |

Out-of-scope work is enumerated in [`docs/roadmap/deferred-backlog.md`](docs/roadmap/deferred-backlog.md). PRs that contradict an entry there are likely to be rejected unless the contributor first argues for re-prioritising the item.

## Evolution

This document describes the Phase 1 model. As the project matures, governance will evolve — likely toward a small steering group with formal voting on contentious changes. That evolution is itself a decision the maintainers will make publicly, via a PR that updates this file.

## Code of conduct

All participants follow the [Contributor Covenant](CODE_OF_CONDUCT.md).
