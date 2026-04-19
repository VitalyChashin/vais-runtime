# Contributing to Vais.Agents

Thanks for considering a contribution. While the project is in Phase 1 (pre-alpha), scope is narrow and cadence is driven by the maintainers — large unsolicited PRs may be rejected on scope even if technically fine. Please open an issue first for anything beyond a bug fix or doc tweak.

## Ground rules

- Apache 2.0 contributions only. By submitting a PR you agree your work is licensed under the project's [LICENSE](LICENSE).
- No AI-stack lock-in in `Vais.Agents.Abstractions` or `Vais.Agents.Core`. Those projects must not reference `Microsoft.SemanticKernel.*`, `Microsoft.Agents.AI.*`, or Orleans. Adapters go in their own package.
- Public API changes require updating `PublicAPI.Unshipped.txt`. The analyzer will tell you what to add.
- Every public type needs an XML doc comment. `TreatWarningsAsErrors` is on.

## Dev loop

```bash
dotnet restore
dotnet build
dotnet test
```

Integration tests that need an `OPENAI_API_KEY` are currently opt-in and skipped in CI.

## Commit style

Conventional commits are appreciated but not required. Keep commit bodies honest about intent — this is a library, not a product.

## Code of conduct

We follow the [Contributor Covenant](CODE_OF_CONDUCT.md).
