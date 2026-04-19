# Choosing a stack: Semantic Kernel vs Microsoft Agent Framework

Vais.Agents supports both **Semantic Kernel** (SK) and **Microsoft Agent Framework** (MAF) behind a single neutral `ICompletionProvider` abstraction. The choice doesn't affect `StatefulAiAgent`, your tools, your guardrails, your guardrail events, your budget, or any higher-level pillar — it only changes which adapter you inject. This page is a decision framework + the parity findings we surfaced building both adapters.

## TL;DR

| Situation | Pick |
|---|---|
| You're already on SK and have a `Kernel` in your DI container | **`Vais.Agents.Ai.SemanticKernel`** |
| You're already on MEAI's `IChatClient` abstraction (or MAF) | **`Vais.Agents.Ai.MicrosoftAgentFramework`** |
| Greenfield, no strong preference | MAF — it's the strategic Microsoft direction for agent primitives; SK remains first-class |
| You need both in the same process (A/B, fallback) | Inject both providers, wire each `StatefulAiAgent` to a different one; no special machinery needed |

Both adapters implement `ICompletionProvider` AND `IStreamingCompletionProvider`, so streaming + non-streaming paths work on either.

## What changes between them

Nothing in the neutral layer. `StatefulAiAgent`, `IAgentSession`, `IMemoryStore`, `IContextProvider`, `ISystemPromptComposer`, guardrails, `RunBudget`, `IToolCallDispatcher`, `AgentEvent` bus, `IUsageSink`, `IAgentRuntime`, `Tool.FromFunc` — identical surface, identical behaviour. The agent class does not know which adapter answered.

What changes: the provider's ctor, what it needs to be handed, and the native machinery it exercises underneath.

```csharp
// SK — give it a Kernel.
var skProvider = new SkCompletionProvider(kernel);

// MAF — give it an IChatClient.
var mafProvider = new MafCompletionProvider(chatClient);
```

## Parity finding: auto-invocation layer

**The single biggest practical difference** between the stacks, and why our outer tool-call loop lives in `StatefulAiAgent` rather than leaning on either stack's built-in auto-invocation:

- **SK's auto-invocation is connector-level.** `OpenAIChatCompletionService` implements it natively; a fake `IChatCompletionService` test double does not. Binding tools as a `KernelPlugin` + setting `FunctionChoiceBehavior.Auto()` only auto-invokes if the underlying connector supports it.
- **MAF's auto-invocation is pipeline-level.** `FunctionInvokingChatClient` is a wrapper that works with any `IChatClient` including fakes.

Vais.Agents v0.4 flipped both adapters to "don't auto-invoke; surface the tool call back to me", and the outer loop in `StatefulAiAgent` dispatches via `IToolCallDispatcher`. SK uses `FunctionChoiceBehavior.None()`; MAF uses `ChatClientAgentOptions.UseProvidedChatClientAsIs = true`. Consumer-visible result: tool calls go through our dispatcher (which runs tool-guardrails, emits `ToolCallStarted` / `ToolCallCompleted` events, enforces `RunBudget.MaxToolCalls`) regardless of which stack is underneath.

## Parity finding: streaming tool accumulation

Both adapters stream text deltas and surface a terminal `CompletionUpdate` carrying any model-requested tool calls:

- **SK**: accumulates streaming `StreamingFunctionCallUpdateContent` fragments via SK's built-in `FunctionCallContentBuilder`. Works naturally because SK's connector implementations emit proper fragments.
- **MAF**: walks `AgentRunResponseUpdate.Contents` for `FunctionCallContent` items and deduplicates by `CallId`. MEAI streams whole `FunctionCallContent` (not arg-string fragments), so last-seen-wins per id is correct.

Either way: `StatefulAiAgent.StreamAsync` sees a terminal `CompletionUpdate.ToolCalls` and loops exactly like `AskAsync`.

## Versioning (current pins)

| Package | Version |
|---|---|
| `Microsoft.SemanticKernel` | 1.74.0 |
| `Microsoft.SemanticKernel.Connectors.OpenAI` | 1.74.0 |
| `Microsoft.Agents.AI` (MAF) | 1.1.0 |
| `Microsoft.Extensions.AI` | 10.5.0 |
| `Microsoft.Extensions.AI.OpenAI` | 10.5.0 |
| `OpenAI` | 2.10.0 |

Both stacks and the OpenAI SDK form a coherent 10.x-aligned graph; earlier SK / MEAI versions have known `OpenAI` floor conflicts we documented away by bumping everything in lockstep.

## Dog-food finding: `ChatRole` namespace collision (resolved)

In v0.1 a consumer file that imported `Vais.Agents` (for our records) AND `Microsoft.Extensions.AI` (to build a custom `IChatClient` for MAF) hit `CS0104` on every `ChatRole.*` use — both libraries shipped a `ChatRole`. We renamed ours to **`AgentChatRole`** in v0.3 (clean break, no public consumers at that stage). A similar collision lurks between `Vais.Agents.IPromptTemplate` and `Microsoft.SemanticKernel.IPromptTemplate` — fully qualify or alias when you import both (`using VaisPromptTemplate = Vais.Agents.IPromptTemplate;`).

## Dog-food finding: `SkCompletionProvider` ctor fail-fast

`new SkCompletionProvider(kernel)` eagerly resolves `IChatCompletionService` from the kernel; a bare `new Kernel()` with no connector registered throws `KernelException` at construction rather than at first call. Fail-fast was the right call — better than a half-constructed provider that dies on every request — but consumers must build the kernel with a chat connector first, then wrap it.

## Next

- [Hello agent](hello-agent.md) — same code on both stacks.
- [Architecture](../concepts/architecture.md) — how adapters fit the broader package layering.
- [Execution loop](../concepts/execution-loop.md) — how the outer tool-call loop works on top of either adapter.
