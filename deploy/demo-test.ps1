#Requires -Version 7
<#
.SYNOPSIS
    Rebuild both demo containers from source and run a Langfuse observability test.

.DESCRIPTION
    1. Stops and removes vais-demo and vais-runtime containers.
    2. Rebuilds both Docker images from the current source tree.
    3. Starts fresh containers (ensuring no stale image layers).
    4. Waits for the runtime to become healthy.
    5. Runs the research-pipeline graph and prints the run ID / Langfuse trace link.

    Reads secrets from deploy/compose/.env (gitignored).

.PARAMETER Endpoint
    The runtime endpoint to invoke the graph on. Defaults to http://localhost:5100 (vais-demo,
    which bundles the Python plugins). Use http://localhost:8080 for vais-runtime (basic runtime).

.PARAMETER Topic
    The research topic to send as graph input. Defaults to a short test topic.

.EXAMPLE
    pwsh deploy/demo-test.ps1
    pwsh deploy/demo-test.ps1 -Topic "LLM routing strategies"
#>
param(
    [string] $Endpoint = "http://localhost:8080",
    [string] $Topic    = "AI agent observability tools in 2025"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path "$PSScriptRoot/..").Path
$EnvFile  = "$PSScriptRoot/compose/.env"

# ── 1. Load secrets from .env ─────────────────────────────────────────────────
if (-not (Test-Path $EnvFile)) {
    Write-Error "Missing $EnvFile — copy deploy/compose/.env.example and fill in secrets."
}

$env_vars = @{}
foreach ($line in Get-Content $EnvFile) {
    if ($line -match '^\s*#' -or $line -match '^\s*$') { continue }
    $key, $val = $line -split '=', 2
    $env_vars[$key.Trim()] = $val.Trim()
}

function Env([string]$key) {
    if (-not $env_vars.ContainsKey($key)) { Write-Error "Missing env var '$key' in .env" }
    return $env_vars[$key]
}

$otel_auth = "Authorization=Basic $(Env 'LANGFUSE_OTEL_AUTH')"
$langfuse_host = Env 'LANGFUSE_HOST'

# ── 2. Stop + remove old containers ──────────────────────────────────────────
Write-Host "`n[1/5] Stopping containers..." -ForegroundColor Cyan
docker stop vais-runtime vais-demo 2>$null
docker rm   vais-runtime vais-demo 2>$null
Write-Host "      Done."

# ── 3. Rebuild images ────────────────────────────────────────────────────────
Write-Host "`n[2/5] Rebuilding vais-research-pipeline:local (full-stack demo image) ..." -ForegroundColor Cyan
Push-Location $RepoRoot
docker build -f samples/PluginAgentResearchPipeline/Dockerfile.demo `
    -t vais-research-pipeline:local . | Select-Object -Last 3
if ($LASTEXITCODE -ne 0) { throw "vais-research-pipeline build failed" }
Pop-Location

# ── 4. Start fresh containers ────────────────────────────────────────────────
Write-Host "`n[3/5] Starting containers..." -ForegroundColor Cyan

docker run -d `
  --name vais-runtime `
  -p 8080:8080 `
  -e "OPENAI_API_KEY=$(Env 'OPENAI_API_KEY')" `
  -e "OTEL_SERVICE_NAME=vais-oss-runtime" `
  -e "LANGFUSE_SECRET_KEY=$(Env 'LANGFUSE_SECRET_KEY')" `
  -e "TAVILY_API_KEY=$(Env 'TAVILY_API_KEY')" `
  -e "OTEL_EXPORTER_OTLP_ENDPOINT=$langfuse_host/api/public/otel" `
  -e "OTEL_EXPORTER_OTLP_HEADERS=$otel_auth" `
  -e "VAIS_LANGFUSE_HOST=$langfuse_host" `
  -e "OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf" `
  -e "VAIS_HOSTING_MODE=localhost" `
  -e "VAIS_LANGFUSE_PROJECT=$(Env 'LANGFUSE_PROJECT')" `
  -e "LANGFUSE_PUBLIC_KEY=$(Env 'LANGFUSE_PUBLIC_KEY')" `
  -e "ASPNETCORE_URLS=http://0.0.0.0:8080" `
  vais-research-pipeline:local | Out-Null

Write-Host "      vais-runtime → http://localhost:8080"

# ── 5. Wait for health ───────────────────────────────────────────────────────
Write-Host "`n[4/5] Waiting for $Endpoint/healthz ..." -ForegroundColor Cyan
$deadline = (Get-Date).AddSeconds(60)
$healthy  = $false
while ((Get-Date) -lt $deadline) {
    try {
        $r = Invoke-WebRequest -Uri "$Endpoint/healthz" -TimeoutSec 2 -UseBasicParsing -EA Stop
        if ($r.StatusCode -eq 200) { $healthy = $true; break }
    } catch { }
    Start-Sleep -Seconds 3
    Write-Host "      ..." -NoNewline
}
Write-Host ""
if (-not $healthy) { throw "Runtime did not become healthy within 60s" }
Write-Host "      Healthy."

# Ensure CLI context points at the right endpoint.
$VaisConfig = "$env:USERPROFILE\.vais\config.yaml"
if (Test-Path $VaisConfig) {
    (Get-Content $VaisConfig) -replace 'currentContext: \S+', 'currentContext: local' |
        Set-Content $VaisConfig
}

# ── 5b. Apply manifests ──────────────────────────────────────────────────────
Write-Host "`n      Applying manifests..." -ForegroundColor Cyan
$ManifestDir = "$RepoRoot/samples/PluginAgentResearchPipeline"
dotnet run --project "$RepoRoot/src/Vais.Agents.Cli/Vais.Agents.Cli.csproj" -- `
    apply -f "$ManifestDir/agents/planner-agent.yaml" --endpoint $Endpoint | Out-Null
dotnet run --project "$RepoRoot/src/Vais.Agents.Cli/Vais.Agents.Cli.csproj" -- `
    apply -f "$RepoRoot/samples/PluginAgentLangGraphResearcherLive/research-agent-live.yaml" --endpoint $Endpoint | Out-Null
dotnet run --project "$RepoRoot/src/Vais.Agents.Cli/Vais.Agents.Cli.csproj" -- `
    apply -f "$ManifestDir/agents/sgr-analyst.yaml" --endpoint $Endpoint | Out-Null
dotnet run --project "$RepoRoot/src/Vais.Agents.Cli/Vais.Agents.Cli.csproj" -- `
    apply -f "$ManifestDir/agents/synthesizer.yaml" --endpoint $Endpoint | Out-Null
dotnet run --project "$RepoRoot/src/Vais.Agents.Cli/Vais.Agents.Cli.csproj" -- `
    apply -f "$ManifestDir/research-pipeline.yaml" --endpoint $Endpoint | Out-Null
Write-Host "      Manifests applied."

# ── 6. Run the pipeline ──────────────────────────────────────────────────────
Write-Host "`n[5/5] Invoking research-pipeline..." -ForegroundColor Cyan
$input_json = "{`"query`":`"$Topic`"}"
$output = dotnet run `
    --project "$RepoRoot/src/Vais.Agents.Cli/Vais.Agents.Cli.csproj" -- `
    invoke-graph research-pipeline `
    --endpoint $Endpoint `
    --stream `
    --initial-state $input_json 2>&1

$output | ForEach-Object { Write-Host "  $_" }

$run_id = ($output | Select-String "run=(\w+)").Matches[0].Groups[1].Value

Write-Host ""
Write-Host "────────────────────────────────────────────────────────" -ForegroundColor Green
Write-Host "  Run ID : $run_id" -ForegroundColor Green
$lf_url = ($langfuse_host -replace 'host\.docker\.internal', 'localhost')
Write-Host "  Langfuse: $lf_url (search Traces tab for run_id=$run_id)" -ForegroundColor Green
Write-Host "────────────────────────────────────────────────────────" -ForegroundColor Green
