#!/usr/bin/env pwsh
# Build every sample. Pass -RunDeterministic to also run the deterministic ones.
param([switch]$RunDeterministic)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# No API key, no Docker, no external services.
# Gateway/PowerFx samples (LlmGatewayMiddleware, McpGatewayMiddleware, OpenAiCompatGateway,
# GraphPowerFxPredicates) require `dotnet pack` for their alpha packages before build succeeds.
$deterministic = @(
  # v0.1–v0.6 library layer
  'PromptComposer', 'CustomMemoryStore', 'ContextProviderRag', 'InputOutputGuardrails',
  'ToolGuardrailsAndInterrupt', 'BudgetEnforcement', 'ToolFromFunc', 'AgentManifestAndRegistry',
  'HelloStreaming', 'HelloStreamingTools', 'SequentialOrchestration', 'RoundRobinOrchestration',
  'HandoffBetweenAgents', 'ObservabilityOtelConsole', 'VectorDataRag',
  'McpToolSourceExample', 'A2ARemoteAgentExample', 'AgentAsToolDelegation',
  # v0.7 MCP inbound
  'McpServerStdio', 'McpServerHttp',
  # v0.8 A2A basics
  'A2AServerBasics',
  # v0.9 graph orchestration
  'AgentGraphInProcess', 'AgentGraphYamlLoader', 'AgentGraphMaf',
  # v0.10 streaming filters + resilience
  'StreamingFilterTypingIndicator', 'StreamingResiliencePolly',
  # v0.11 HTTP polish
  'HttpIdempotencyInMemory', 'OpenApiSpecExplorer',
  # v0.12 HTTP streaming
  'HttpStreamingInvoke', 'HttpStreamingCancellation',
  # v0.40 gateway middleware (pack first: src/Vais.Agents.Gateways.*)
  'LlmGatewayMiddleware', 'McpGatewayMiddleware', 'OpenAiCompatGateway',
  # v0.42 live-mode HITL
  'GraphHitlLiveMode',
  # v0.53 PowerFx predicates (pack first: src/Vais.Agents.Core.PowerFx)
  'GraphPowerFxPredicates'
)

$liveLlm = @('HelloAgent')  # OPENAI_API_KEY required
$docker   = @('OrleansSilo', 'OrleansRedisPersistence', 'OrleansPostgresPersistence')
$orleans  = @('A2AInterruptResumeOrleans', 'AgentGraphResumeOnOrleans')  # in-process silo; no Docker
$opa      = @('OpaPolicyGateLocal')  # requires: opa run --server samples/OpaPolicyGateLocal/policy.rego

$all = $deterministic + $liveLlm + $docker + $orleans + $opa
Write-Host "=== building $($all.Count) samples ==="
foreach ($d in $all) {
    Write-Host "--- build $d ---"
    dotnet build $d -c Release --nologo | Select-Object -Last 4
}

if ($RunDeterministic) {
    Write-Host ""
    Write-Host "=== running deterministic samples ==="
    foreach ($d in $deterministic) {
        Write-Host "--- run $d ---"
        dotnet run --project $d -c Release --nologo
        Write-Host ""
    }
}
