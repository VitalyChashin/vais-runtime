# Deep agent development

Audience: you're authoring agent code that the runtime supervises.

You'll author a C# plugin (in-process DLL), then a from-scratch LangGraph plugin in Python, then a language-neutral container plugin in Go. Same durability contract across all three — the grain is the anchor; the plugin (in-process or out-of-process) is ephemeral.

## Path

1. **[Author a C# plugin](author-a-csharp-plugin.md)** — code-authored `IAiAgent` DLL, loaded at silo startup, supervised by the agent grain.
2. **[Build a LangGraph plugin](build-a-langgraph-plugin.md)** — minimal Python plugin with a real LangGraph state graph (`classify → respond`), packaged as a container, deployed via `vais apply`. A larger reference example: [`samples/PluginAgentLangGraphResearcherLive/`](../../samples/PluginAgentLangGraphResearcherLive/).
3. **[Author a container plugin in Go](author-a-container-plugin-in-go.md)** — IP-1 HTTP protocol documented generically with Go (`net/http`) as the worked example. No SDK required — any language that can serve HTTP can author a plugin. Paired sample: [`samples/quickstart-go-plugin/`](../../samples/quickstart-go-plugin/).

## What you're really learning

The grain is the durability anchor. The plugin (in-process or out-of-process, any language) is ephemeral. State persists in `IPersistentState<AiAgentGrainState>` regardless of authoring language — the plugin returns its opaque state on every turn and gets it back on activation. A silo restart, pod roll, or plugin-container replacement is invisible to the conversation.

## After this section

- Customizing the runtime's extension seams (middleware, guardrails, providers) → [Extensions](../extensions/index.md)
- Embedding the library in a .NET app instead of authoring a plugin → [Library mode](../library-mode/index.md)

## Related

- [Concepts → Runtime plugins](../concepts/runtime-plugins.md) — the plugin-loading model and ABI.
- [Concepts → Polyglot plugins](../concepts/polyglot-plugins.md) — subprocess plugins (Python today).
- [Concepts → Polyglot agents](../concepts/polyglot-agents.md) — declarative + foreign-language agents.
