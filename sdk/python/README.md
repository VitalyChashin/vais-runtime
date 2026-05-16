# vais-plugin Python SDK

Python SDK for building Vais.Agents container plugins. Implements the IP-1 HTTP protocol so Python code can participate in agent graphs, receive LLM-enriched prompts, call the runtime's LLM and tool gateways, and return structured responses.

## Installation

```bash
pip install vais-plugin
```

## Quick start

```python
from vais_plugin import PluginAgent, vais_plugin, InvokeRequest, InvokeResponse

@vais_plugin
class MyAgent(PluginAgent):
    async def invoke(self, request: InvokeRequest) -> InvokeResponse:
        reply = await self.llm.chat(request.messages)
        return InvokeResponse(message=reply.content)
```

## P12 import guard

The SDK enforces the P12 mandatory-outbound contract by blocking direct imports of LLM provider SDKs. When the SDK is imported, a `MetaPathFinder` is inserted into `sys.meta_path` that raises `ImportError` for any of the following packages:

| Blocked | Why |
|---|---|
| `openai` | Must use `vais_plugin.AsyncLlmClient` → `request.llm_gateway_url` |
| `anthropic` | Same |
| `cohere` | Same |
| `mistralai` | Same |
| `litellm` | Same |
| `langchain_openai` | Same |
| `langchain_anthropic` | Same |
| `google.generativeai` | Same |

Packages like `httpx`, `requests`, `aiohttp`, `google.protobuf`, and stdlib modules are not blocked.

### Why

Direct provider SDK imports bypass the runtime's LLM gateway middleware chain (rate limiting, token budgets, OTel tracing, Langfuse enrichment). They also create unmediated external network paths that undermine P12 egress isolation. Every LLM call must exit via `request.llm_gateway_url`; every tool call via `request.tool_gateway_url`.

### Disabling for local development

Set the environment variable before importing the SDK:

```bash
export VAIS_PLUGIN_DISABLE_IMPORT_GUARD=1
python my_plugin.py
```

A warning is logged to stderr when the guard is disabled. **Do not set this variable in production.**

### Testing your plugin with the guard disabled

```bash
VAIS_PLUGIN_DISABLE_IMPORT_GUARD=1 pytest tests/
```

Or in `pyproject.toml`:

```toml
[tool.pytest.ini_options]
env = ["VAIS_PLUGIN_DISABLE_IMPORT_GUARD=1"]
```

## Guides

- [Build a LangGraph plugin](../../docs/deep-development/build-a-langgraph-plugin.md)
- [P12 plugin sandbox contract](../../docs/concepts/control-plane.md)
- [Agent input middleware](../../docs/extensions/agent-input-middleware.md)
