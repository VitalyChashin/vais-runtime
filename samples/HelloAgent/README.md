# HelloAgent

Runs the same `StatefulAiAgent` through Semantic Kernel, then Microsoft Agent Framework, then a third tool-calling scenario. The point: one stack-neutral agent class drives both stacks without change.

**Concepts:** [hello-agent walkthrough](../../docs/getting-started/hello-agent.md), [choosing a stack](../../docs/getting-started/choosing-a-stack.md).
**Packages:** `Vais.Agents.Abstractions`, `Vais.Agents.Core`, `Vais.Agents.Ai.SemanticKernel`, `Vais.Agents.Ai.MicrosoftAgentFramework`.
**Needs API key:** `OPENAI_API_KEY` (live-LLM sample).

## Run

```bash
OPENAI_API_KEY=sk-... dotnet run --project samples/HelloAgent
```

## What you'll see

1. SK-adapter conversation (two turns, history carries).
2. MAF-adapter conversation (same structure, different stack underneath).
3. Tool-calling turn through both stacks, with a trivial `RollDiceTool`.
