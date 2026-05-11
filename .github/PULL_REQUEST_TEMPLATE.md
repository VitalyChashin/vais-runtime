## Summary

<!-- One or two sentences. What changes and why. Honest about intent — not marketing copy. -->

## Type of change

<!-- Tick all that apply. Remove the rest. -->

- [ ] Bug fix
- [ ] New feature
- [ ] Breaking change
- [ ] Docs / samples only
- [ ] Refactor / test / build hygiene

## Checklist

- [ ] Tests added or updated. Bug fixes in library code **must** be covered by a test — see [AGENTS.md §Build & test](../AGENTS.md).
- [ ] `PublicAPI.Unshipped.txt` updated for any new or changed public API. RS0016 / RS0017 will fail CI otherwise.
- [ ] `CHANGELOG.md` updated under `## [Unreleased]` with the correct Keep-a-Changelog category (`Added` / `Changed` / `Fixed` / `Removed` / `Deprecated` / `Security`). Breaking changes go under `Changed` with migration guidance.
- [ ] XML docs added for every new public type or member. `GenerateDocumentationFile = true` + `CS1591 → error` will fail CI otherwise.
- [ ] Docs touched if behaviour or API changed (`docs/concepts/` for design, `docs/guides/` for recipes, `docs/reference/` for tables).
- [ ] Sample added or updated if this introduces a feature worth demonstrating.
- [ ] `dotnet build` + `dotnet test` clean locally on at least one platform.

## Related issues / discussions

<!-- "Closes #123" / "Refs #456" — link the issue that motivated this PR. -->

## Migration notes

<!-- For breaking changes only: what callers must do (old signature → new signature, config rename, etc.). Delete this section if not applicable. -->

## Test evidence

<!-- For non-trivial changes: paste the relevant test output, a screenshot of the sample run, or the Langfuse trace URL. -->
