# CustomMemoryStore

Implements [`IMemoryStore`](../../docs/concepts/session.md#core-types) as a file-backed JSON dict. Writes + reads + searches across three scopes (session / agent / tenant) and shows scope isolation.

**Concepts:** [session + memory](../../docs/concepts/session.md).
**Packages:** `Vais.Agents.Abstractions`, `Vais.Agents.Core`.
**Needs API key:** no.

```bash
dotnet run --project samples/CustomMemoryStore
```
