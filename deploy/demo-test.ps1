#Requires -Version 7
<#
.SYNOPSIS
    Full rebuild of the demo stack and end-to-end research-pipeline run.

.DESCRIPTION
    1. Stops the vais-local-dev Compose stack.
    2. Rebuilds vais-research-pipeline:local and vais-tavily-mcp:local from source.
    3. Ensures the Orleans schema exists in pgvector-db-1.
    4. Starts the stack (vais-runtime + tavily-mcp on vais2-network).
    5. Waits for /healthz, then applies all manifests in dependency order.
    6. Invokes the research-pipeline graph and prints the run ID + Langfuse link.

    Reads secrets from docs/local-dev.env (workspace root, gitignored).
    Writes local-dev/.env for Docker Compose interpolation.

.PARAMETER Topic
    The research topic to send as graph input. Defaults to a short test topic.

.EXAMPLE
    pwsh deploy/demo-test.ps1
    pwsh deploy/demo-test.ps1 -Topic "LLM routing strategies"
#>
param(
    [string] $Topic = "AI agent observability tools in 2025"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$AgenticRoot   = (Resolve-Path "$PSScriptRoot/..").Path
$WorkspaceRoot = (Resolve-Path "$AgenticRoot/..").Path
$LocalDevDir   = "$WorkspaceRoot/local-dev"
$SecretsFile   = "$WorkspaceRoot/docs/local-dev.env"
$ManifestDir   = "$AgenticRoot/samples/PluginAgentResearchPipeline"
$CliProject    = "$AgenticRoot/src/Vais.Agents.Cli/Vais.Agents.Cli.csproj"
$Endpoint      = "http://localhost:8080"

# ── Load secrets ──────────────────────────────────────────────────────────────
if (-not (Test-Path $SecretsFile)) {
    Write-Error "Missing $SecretsFile — copy docs/local-dev.env.example and fill in secrets."
}
$Secrets = @{}
foreach ($line in Get-Content $SecretsFile) {
    if ($line -match '^\s*#' -or $line -match '^\s*$') { continue }
    $k, $v = $line -split '=', 2
    $Secrets[$k.Trim()] = $v.Trim()
}
function Get-Secret([string] $key) {
    if (-not $Secrets.ContainsKey($key)) { Write-Error "Missing '$key' in $SecretsFile" }
    return $Secrets[$key]
}

$langfusePublicKey = Get-Secret 'LANGFUSE_PUBLIC_KEY'
$langfuseSecretKey = Get-Secret 'LANGFUSE_SECRET_KEY'
$langfuseHost      = Get-Secret 'LANGFUSE_HOST'
$otelB64           = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("${langfusePublicKey}:${langfuseSecretKey}"))
$otelHeader        = "Authorization=Basic $otelB64"
$langfuseLocalUrl  = $langfuseHost -replace 'langfuse-langfuse-web-1', 'localhost'

# Write compose .env for variable interpolation
@"
LANGFUSE_PUBLIC_KEY=$langfusePublicKey
LANGFUSE_SECRET_KEY=$langfuseSecretKey
LANGFUSE_HOST=$langfuseHost
OTEL_EXPORTER_OTLP_HEADERS=$otelHeader
OPENAI_API_KEY=$(Get-Secret 'OPENAI_API_KEY')
TAVILY_API_KEY=$(Get-Secret 'TAVILY_API_KEY')
"@ | Set-Content "$LocalDevDir/.env" -Encoding UTF8

# ── Helpers ───────────────────────────────────────────────────────────────────

function Invoke-Compose([string[]] $ComposeArgs) {
    Push-Location $LocalDevDir
    try { docker compose -f docker-compose.dev.yml @ComposeArgs }
    finally { Pop-Location }
}

function Wait-Health([int] $timeoutSec = 120) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        try {
            $r = Invoke-WebRequest -Uri "$Endpoint/healthz" -TimeoutSec 3 -UseBasicParsing -EA Stop
            if ($r.StatusCode -eq 200) { Write-Host "      Healthy." ; return }
        } catch { }
        Start-Sleep -Seconds 4
        Write-Host "      ..." -NoNewline
    }
    Write-Host ""
    throw "Runtime did not become healthy within ${timeoutSec}s"
}

function Ensure-OrleansSchema {
    $mainOk = "$(docker exec pgvector-db-1 psql -U testuser -d vectordb -tAc `
        "SELECT 1 FROM information_schema.tables WHERE table_schema='public' AND table_name='orleansquery' LIMIT 1")".Trim()
    $persistOk = "$(docker exec pgvector-db-1 psql -U testuser -d vectordb -tAc `
        "SELECT 1 FROM information_schema.tables WHERE table_schema='public' AND table_name='orleansstorage' LIMIT 1")".Trim()

    if ($mainOk -eq "1" -and $persistOk -eq "1") {
        Write-Host "      Orleans schema present — skipping."
        return
    }
    if ($mainOk -ne "1") {
        $sqlMain = "$AgenticRoot/tests/Vais.Agents.Persistence.Postgres.Tests/Sql/PostgreSQL-Main.sql"
        docker cp $sqlMain pgvector-db-1:/tmp/PostgreSQL-Main.sql
        docker exec pgvector-db-1 psql -U testuser -d vectordb -f /tmp/PostgreSQL-Main.sql | Out-Null
        docker exec pgvector-db-1 rm /tmp/PostgreSQL-Main.sql
    }
    $sqlPersist = "$AgenticRoot/tests/Vais.Agents.Persistence.Postgres.Tests/Sql/PostgreSQL-Persistence.sql"
    docker cp $sqlPersist pgvector-db-1:/tmp/PostgreSQL-Persistence.sql
    docker exec pgvector-db-1 psql -U testuser -d vectordb -f /tmp/PostgreSQL-Persistence.sql | Out-Null
    docker exec pgvector-db-1 rm /tmp/PostgreSQL-Persistence.sql
    Write-Host "      Orleans schema applied."
}

function Invoke-Apply([string] $file) {
    $out = dotnet run --project $CliProject -- apply -f $file --endpoint $Endpoint 2>&1
    if ($LASTEXITCODE -ne 0) { throw "apply failed for $(Split-Path $file -Leaf)`n$out" }
}

# ── [1/5] Stop ────────────────────────────────────────────────────────────────
Write-Host "`n[1/5] Stopping vais-local-dev stack..." -ForegroundColor Cyan
Invoke-Compose "down"
# Remove any stray containers left by previous bare `docker run` invocations.
docker rm -f vais-runtime vais-tavily-mcp 2>$null; $true
Write-Host "      Done."

# ── [2/5] Build ───────────────────────────────────────────────────────────────
Write-Host "`n[2/5] Rebuilding images from source..." -ForegroundColor Cyan
Invoke-Compose "build"
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

# ── [3/5] Orleans schema ──────────────────────────────────────────────────────
Write-Host "`n[3/5] Checking Orleans schema..." -ForegroundColor Cyan
Ensure-OrleansSchema

# ── [4/5] Start + health + manifests ─────────────────────────────────────────
Write-Host "`n[4/5] Starting stack..." -ForegroundColor Cyan
Invoke-Compose "up", "-d"
if ($LASTEXITCODE -ne 0) { throw "Compose up failed" }
Write-Host "      vais-runtime → $Endpoint"

Write-Host "`n      Waiting for $Endpoint/healthz ..."
Wait-Health

$VaisConfig = "$env:USERPROFILE\.vais\config.yaml"
if (Test-Path $VaisConfig) {
    (Get-Content $VaisConfig) -replace 'currentContext: \S+', 'currentContext: local' |
        Set-Content $VaisConfig
}

Write-Host "`n      Applying manifests..."
# Gateways first — agents reference them by ID at activation time.
Invoke-Apply "$ManifestDir/gateways/llm-gateway.yaml"
Invoke-Apply "$ManifestDir/gateways/mcp-gateway.yaml"
Invoke-Apply "$ManifestDir/gateways/mcp-server-tavily.yaml"
Invoke-Apply "$ManifestDir/agents/planner-agent.yaml"
Invoke-Apply "$AgenticRoot/samples/PluginAgentLangGraphResearcherLive/research-agent-live.yaml"
Invoke-Apply "$ManifestDir/agents/sgr-analyst.yaml"
Invoke-Apply "$ManifestDir/agents/synthesizer.yaml"
Invoke-Apply "$ManifestDir/research-pipeline.yaml"
Write-Host "      Manifests applied."

# ── [5/5] Invoke pipeline ─────────────────────────────────────────────────────
Write-Host "`n[5/5] Invoking research-pipeline..." -ForegroundColor Cyan
$inputJson = "{`"query`":`"$Topic`"}"
$output = dotnet run --project $CliProject -- `
    invoke-graph research-pipeline `
    --endpoint $Endpoint `
    --stream `
    --initial-state $inputJson 2>&1

$output | ForEach-Object { Write-Host "  $_" }

$runId = ($output | Select-String "run=(\w+)").Matches[0].Groups[1].Value

Write-Host ""
Write-Host "────────────────────────────────────────────────────────" -ForegroundColor Green
Write-Host "  Run ID : $runId" -ForegroundColor Green
Write-Host "  Langfuse: $langfuseLocalUrl (search Traces tab for run_id=$runId)" -ForegroundColor Green
Write-Host "────────────────────────────────────────────────────────" -ForegroundColor Green
