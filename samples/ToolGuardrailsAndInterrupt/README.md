# ToolGuardrailsAndInterrupt

Tool guardrail interrupts when the model requests a destructive tool. Catches `AgentInterruptedException`, simulates a human approval, resumes.

**Concepts:** [guardrails](../../docs/concepts/guardrails.md), [execution loop](../../docs/concepts/execution-loop.md).
**Packages:** `Vais.Agents.Abstractions`, `Vais.Agents.Core`.
**Needs API key:** no.

```bash
dotnet run --project samples/ToolGuardrailsAndInterrupt
```
