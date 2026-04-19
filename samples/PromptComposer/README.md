# PromptComposer

Demonstrates [`AggregatingSystemPromptComposer`](../../docs/concepts/prompt.md) + two `ISystemPromptContributor`s at different priorities. Drives one turn through a scripted fake provider so the composed system prompt is observable.

**Concepts:** [prompt](../../docs/concepts/prompt.md).
**Packages:** `Vais.Agents.Abstractions`, `Vais.Agents.Core`.
**Needs API key:** no.

```bash
dotnet run --project samples/PromptComposer
```

Output prints the composed system prompt the provider sees (persona + tenant-policy joined by `\n\n`) plus the scripted reply.
