#Requires -Version 7
<#
.SYNOPSIS
    Smoke test: Orleans grain drain on SIGTERM (docker stop).

.DESCRIPTION
    Verifies that `docker stop -t 45` triggers a graceful Orleans silo shutdown
    that calls OnDeactivateAsync on active session grains before the process exits.

    Positive run (default):
      1. Builds vais-agents-runtime:smoke-test from source (or reuses with -SkipBuild).
      2. Starts the runtime with a minimal boot manifest so smoke-agent is registered.
      3. Invokes smoke-agent with X-Session-Id, activating the AiAgentGrain.
      4. Runs `docker stop -t 45`.
      5. Asserts "Grain deactivating on shutdown" appears in `docker logs` — proving
         the grain drained within the 30 s host budget (VAIS_SHUTDOWN_TIMEOUT_SECONDS).

    Negative control (-NegativeControl):
      Same flow but with VAIS_SHUTDOWN_TIMEOUT_SECONDS=5.
      Asserts the deactivation line is ABSENT — proving the guard is load-bearing.

.PARAMETER SkipBuild
    Skip rebuilding the image; use the existing vais-agents-runtime:smoke-test tag.

.PARAMETER NegativeControl
    Run only the negative-control cycle (5 s timeout; asserts deactivation line absent).

.PARAMETER Port
    Host port to bind. Defaults to 18080 to avoid collisions with a running runtime on 8080.

.EXAMPLE
    pwsh deploy/shutdown-drain-test.ps1
    pwsh deploy/shutdown-drain-test.ps1 -SkipBuild
    pwsh deploy/shutdown-drain-test.ps1 -NegativeControl
#>
param(
    [switch] $SkipBuild,
    [switch] $NegativeControl,
    [int]    $Port = 18080
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$AgenticRoot = (Resolve-Path "$PSScriptRoot/..").Path
$Image       = "vais-agents-runtime:smoke-test"
$Container   = "vais-shutdown-smoke"
$Endpoint    = "http://localhost:$Port"
$SessionId   = "smoke-session-$(Get-Date -Format 'yyyyMMddHHmmss')"

# Minimal agent manifest written to a temp boot-manifests directory.
# The grain activates when the invoke arrives; the LLM call fails (no key) but the
# grain stays in-memory until idle collection (~2 h). That is the grain we drain.
$BootManifest = @'
apiVersion: vais.agents/v1
kind: Agent
metadata:
  name: smoke-agent
spec:
  description: Drain smoke-test agent — no real LLM needed.
  model:
    provider: openai
    id: gpt-4o-mini
'@

function Remove-SmokeContainer {
    docker rm -f $Container 2>$null | Out-Null
}

# ── Build ──────────────────────────────────────────────────────────────────────
if (-not $SkipBuild) {
    Write-Host "Building $Image from source ..." -ForegroundColor Cyan
    docker build -f "$AgenticRoot/src/Vais.Agents.Runtime.Host/Dockerfile" `
                 -t $Image "$AgenticRoot"
    if ($LASTEXITCODE -ne 0) { throw "docker build failed." }
}

# ── Smoke cycle ────────────────────────────────────────────────────────────────
function Invoke-SmokeCycle([int] $ShutdownTimeout, [bool] $ExpectDrain) {
    Remove-SmokeContainer

    # Write boot manifest to a temp directory; mount it into the container.
    $TmpManifestDir = Join-Path ([System.IO.Path]::GetTempPath()) "vais-smoke-$([System.Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $TmpManifestDir | Out-Null
    Set-Content -Path "$TmpManifestDir/smoke-agent.yaml" -Value $BootManifest

    Write-Host "`nStarting container (shutdownTimeout=${ShutdownTimeout}s, port=$Port) ..." -ForegroundColor Cyan

    docker run -d --name $Container `
        -p "${Port}:8080" `
        -v "${TmpManifestDir}:/var/lib/vais/smoke-manifests:ro" `
        -e VAIS_HOSTING_MODE=localhost `
        -e ASPNETCORE_URLS=http://0.0.0.0:8080 `
        -e VAIS_SHUTDOWN_TIMEOUT_SECONDS=$ShutdownTimeout `
        -e VAIS_BOOT_MANIFESTS_DIRECTORY=/var/lib/vais/smoke-manifests `
        -e OPENAI_API_KEY=smoke-test-placeholder `
        $Image | Out-Null

    if ($LASTEXITCODE -ne 0) {
        throw "docker run failed."
    }

    # ── Wait for health ────────────────────────────────────────────────────────
    $deadline = (Get-Date).AddSeconds(90)
    Write-Host "Waiting for /healthz ..." -NoNewline
    $healthy = $false
    while ((Get-Date) -lt $deadline) {
        try {
            $r = Invoke-WebRequest -Uri "$Endpoint/healthz" -UseBasicParsing -TimeoutSec 2 -ErrorAction Stop
            if ($r.StatusCode -eq 200) { $healthy = $true; Write-Host " healthy."; break }
        } catch {}
        Write-Host -NoNewline "."
        Start-Sleep 3
    }
    if (-not $healthy) {
        Write-Host ""
        Write-Host "[FAIL] Runtime did not become healthy within 90 s." -ForegroundColor Red
        docker logs $Container 2>&1 | Select-Object -Last 30 | ForEach-Object { Write-Host "  $_" }
        Remove-SmokeContainer
        Remove-Item -Recurse -Force $TmpManifestDir -ErrorAction SilentlyContinue
        return $false
    }

    # ── Activate the session grain ─────────────────────────────────────────────
    # The invoke will fail at the LLM layer (no real API key) but AiAgentGrain.OnActivateAsync
    # has already run — the grain is live in the silo. It stays active until idle collection
    # (~2 h default), so it will be present when we stop the silo below.
    Write-Host "Invoking smoke-agent (activating AiAgentGrain) ..." -NoNewline
    try {
        Invoke-RestMethod -Uri "$Endpoint/v1/agents/smoke-agent/invoke" -Method Post `
            -ContentType "application/json" `
            -Body '{"input":"ping"}' `
            -Headers @{ "X-Session-Id" = $SessionId } `
            -TimeoutSec 10 -ErrorAction SilentlyContinue | Out-Null
    } catch {
        # Expected — no real LLM. Grain is now active regardless.
    }
    Write-Host " done (invoke may have errored; grain is active)."
    Start-Sleep 2  # Let grain activation settle in the activation directory.

    # ── Graceful stop ──────────────────────────────────────────────────────────
    Write-Host "Stopping container (docker stop -t 45) ..." -ForegroundColor Cyan
    docker stop -t 45 $Container | Out-Null
    Start-Sleep 1  # Give Docker a moment to flush log buffers.

    $logs = (docker logs $Container 2>&1) -join "`n"
    $drainLine = ($logs -split "`n") | Where-Object { $_ -match "Grain deactivating on shutdown" } | Select-Object -First 1

    Remove-SmokeContainer
    Remove-Item -Recurse -Force $TmpManifestDir -ErrorAction SilentlyContinue

    if ($ExpectDrain) {
        if ($drainLine) {
            Write-Host "[PASS] Drain line found:" -ForegroundColor Green
            Write-Host "       $($drainLine.Trim())"
            return $true
        } else {
            Write-Host "[FAIL] 'Grain deactivating on shutdown' not found in logs." -ForegroundColor Red
            Write-Host "Last 40 log lines:"
            ($logs -split "`n") | Select-Object -Last 40 | ForEach-Object { Write-Host "  $_" }
            return $false
        }
    } else {
        if ($drainLine) {
            Write-Host "[FAIL] Drain line found unexpectedly in negative-control run:" -ForegroundColor Red
            Write-Host "       $($drainLine.Trim())"
            return $false
        } else {
            Write-Host "[PASS] Drain line absent as expected (5 s host timeout cuts the drain before grains are reached)." -ForegroundColor Green
            return $true
        }
    }
}

# ── Run cycles ─────────────────────────────────────────────────────────────────
$passed = $true

if (-not $NegativeControl) {
    Write-Host "`n=== POSITIVE: VAIS_SHUTDOWN_TIMEOUT_SECONDS=30 — expect graceful grain drain ===" -ForegroundColor Yellow
    $passed = $passed -and (Invoke-SmokeCycle -ShutdownTimeout 30 -ExpectDrain $true)
}

Write-Host "`n=== NEGATIVE CONTROL: VAIS_SHUTDOWN_TIMEOUT_SECONDS=5 — expect drain cut off ===" -ForegroundColor Yellow
$passed = $passed -and (Invoke-SmokeCycle -ShutdownTimeout 5 -ExpectDrain $false)

Write-Host ""
if ($passed) {
    Write-Host "All shutdown-drain smoke checks PASSED." -ForegroundColor Green
    exit 0
} else {
    Write-Host "One or more shutdown-drain smoke checks FAILED." -ForegroundColor Red
    exit 1
}
