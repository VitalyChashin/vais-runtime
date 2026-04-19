#!/usr/bin/env bash
# Build every sample. Pass RUN_DETERMINISTIC=1 to also run the deterministic samples (no API key, no Docker required).
set -euo pipefail
cd "$(dirname "$0")"

DETERMINISTIC=(
  PromptComposer CustomMemoryStore ContextProviderRag InputOutputGuardrails
  ToolGuardrailsAndInterrupt BudgetEnforcement ToolFromFunc AgentManifestAndRegistry
  HelloStreaming HelloStreamingTools SequentialOrchestration RoundRobinOrchestration
  HandoffBetweenAgents ObservabilityOtelConsole VectorDataRag
  McpToolSourceExample A2ARemoteAgentExample
)
LIVE_LLM=( HelloAgent )
DOCKER=( OrleansSilo OrleansRedisPersistence OrleansPostgresPersistence )

echo "=== building ${#DETERMINISTIC[@]} + ${#LIVE_LLM[@]} + ${#DOCKER[@]} samples ==="
for d in "${DETERMINISTIC[@]}" "${LIVE_LLM[@]}" "${DOCKER[@]}"; do
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
