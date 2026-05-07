#!/usr/bin/env bash
# Build every sample. Pass RUN_DETERMINISTIC=1 to also run the deterministic samples (no API key, no Docker required).
# Gateway/PowerFx samples (LlmGatewayMiddleware, McpGatewayMiddleware, OpenAiCompatGateway,
# GraphPowerFxPredicates) require `dotnet pack` for their alpha packages before build succeeds.
set -euo pipefail
cd "$(dirname "$0")"

# v0.1–v0.6 library layer
CORE=(
  PromptComposer CustomMemoryStore ContextProviderRag InputOutputGuardrails
  ToolGuardrailsAndInterrupt BudgetEnforcement ToolFromFunc AgentManifestAndRegistry
  HelloStreaming HelloStreamingTools SequentialOrchestration RoundRobinOrchestration
  HandoffBetweenAgents ObservabilityOtelConsole VectorDataRag
  McpToolSourceExample A2ARemoteAgentExample
)
# v0.7 MCP inbound
MCP=( McpServerStdio McpServerHttp )
# v0.8 A2A basics
A2A=( A2AServerBasics )
# v0.9 graph orchestration
GRAPH=( AgentGraphInProcess AgentGraphYamlLoader AgentGraphMaf )
# v0.10/v0.11/v0.12 streaming + HTTP polish
HTTP=(
  StreamingFilterTypingIndicator StreamingResiliencePolly
  HttpIdempotencyInMemory OpenApiSpecExplorer
  HttpStreamingInvoke HttpStreamingCancellation
)
# v0.40 gateway middleware (pack first: src/Vais.Agents.Gateways.*)
GATEWAY=( LlmGatewayMiddleware McpGatewayMiddleware OpenAiCompatGateway )
# v0.42 live HITL + v0.53 PowerFx predicates (PowerFx: pack first: src/Vais.Agents.Core.PowerFx)
GRAPH2=( GraphHitlLiveMode GraphPowerFxPredicates )

DETERMINISTIC=( "${CORE[@]}" "${MCP[@]}" "${A2A[@]}" "${GRAPH[@]}" "${HTTP[@]}" "${GATEWAY[@]}" "${GRAPH2[@]}" )
LIVE_LLM=( HelloAgent )                                         # OPENAI_API_KEY required
DOCKER=( OrleansSilo OrleansRedisPersistence OrleansPostgresPersistence )
ORLEANS=( A2AInterruptResumeOrleans AgentGraphResumeOnOrleans ) # in-process silo; no Docker
OPA=( OpaPolicyGateLocal )                                      # requires: opa run --server samples/OpaPolicyGateLocal/policy.rego

ALL=( "${DETERMINISTIC[@]}" "${LIVE_LLM[@]}" "${DOCKER[@]}" "${ORLEANS[@]}" "${OPA[@]}" )

echo "=== building ${#ALL[@]} samples ==="
for d in "${ALL[@]}"; do
  echo "--- build $d ---"
  dotnet build "$d" -c Release --nologo | tail -4
done

if [[ "${RUN_DETERMINISTIC:-0}" == "1" ]]; then
  echo
  echo "=== running deterministic samples ==="
  for d in "${DETERMINISTIC[@]}"; do
    echo "--- run $d ---"
    dotnet run --project "$d" -c Release --nologo
    echo
  done
fi
