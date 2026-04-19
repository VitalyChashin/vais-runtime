#!/usr/bin/env pwsh
# Build every sample. Pass -RunDeterministic to also run the deterministic ones.
param([switch]$RunDeterministic)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$deterministic = @(
  'PromptComposer', 'CustomMemoryStore', 'ContextProviderRag', 'InputOutputGuardrails',
  'ToolGuardrailsAndInterrupt', 'BudgetEnforcement', 'ToolFromFunc', 'AgentManifestAndRegistry',
  'HelloStreaming', 'HelloStreamingTools', 'SequentialOrchestration', 'RoundRobinOrchestration',
  'HandoffBetweenAgents', 'ObservabilityOtelConsole', 'VectorDataRag',
  'McpToolSourceExample', 'A2ARemoteAgentExample'
)
$liveLlm = @('HelloAgent')
$docker = @('OrleansSilo', 'OrleansRedisPersistence', 'OrleansPostgresPersistence')

Write-Host "=== building $($deterministic.Count + $liveLlm.Count + $docker.Count) samples ==="
foreach ($d in $deterministic + $liveLlm + $docker) {
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
