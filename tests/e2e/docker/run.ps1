# E2E Docker Suite — tests container plugin Docker-standalone topology.
#
# The runtime runs directly on the host (not in Docker) so DockerContainerSupervisor
# can reach the local Docker daemon and create the echo plugin container without
# Docker-in-Docker complexity.
#
# Prerequisites:
#   - dotnet 9 SDK on PATH
#   - docker on PATH and daemon running
#   - vais CLI on PATH (or set $VaisExe)

param(
    [switch]$KeepUp,              # skip teardown (leave runtime running for debugging)
    [string]$VaisExe = "vais",    # override if vais is not on PATH
    [int]$RuntimePort = 18080     # host port for the runtime HTTP API
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$here     = $PSScriptRoot
$e2eRoot  = Resolve-Path "$here\.."           # tests/e2e/
$repoRoot = Resolve-Path "$here\..\..\.."     # agentic/
$passed = 0
$failed = 0

function Assert([string]$label, [bool]$condition) {
    if ($condition) {
        Write-Host "  PASS  $label" -ForegroundColor Green
        $script:passed++
    } else {
        Write-Host "  FAIL  $label" -ForegroundColor Red
        $script:failed++
    }
}

function Wait-ForRuntime([int]$timeoutSec = 60) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    do {
        try {
            $r = Invoke-WebRequest "http://localhost:$RuntimePort/healthz" -UseBasicParsing -ErrorAction SilentlyContinue
            if ($r.StatusCode -eq 200) { return $true }
        } catch { }
        Start-Sleep 2
    } until ((Get-Date) -gt $deadline)
    return $false
}

function Wait-ForPlugin([string]$name, [string]$state = "Ready", [int]$timeoutSec = 60) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    do {
        try {
            $json = & $VaisExe plugin-status --output json 2>$null | ConvertFrom-Json
            $p = $json.items | Where-Object { $_.name -eq $name }
            if ($p -and $p.state -eq $state) { return $p }
        } catch { }
        Start-Sleep 3
    } until ((Get-Date) -gt $deadline)
    return $null
}

# ── Setup ─────────────────────────────────────────────────────────────────────
Write-Host "`n=== E2E Docker Suite ===" -ForegroundColor Cyan

# Write a temporary vais config pointing at the local runtime
$tmpConfig = [System.IO.Path]::GetTempFileName() -replace '\.tmp$', '.yaml'
@"
apiVersion: vais.io/v1
kind: Config
currentContext: e2e
clusters:
  - name: e2e
    server: http://localhost:$RuntimePort
contexts:
  - name: e2e
    cluster: e2e
    user: ""
users: []
"@ | Set-Content $tmpConfig
$env:VAIS_CONFIG = $tmpConfig

Write-Host "[1/6] Build echo plugin images"
docker build -t vais-echo:test  "$e2eRoot\shared\echo-plugin" -q | Out-Null
docker tag vais-echo:test vais-echo:test-v2

Write-Host "[2/6] Publish + start runtime (no filesystem plugins)"
$publishDir = Join-Path $env:TEMP "vais-e2e-runtime"
New-Item -ItemType Directory -Force $publishDir | Out-Null
dotnet publish "$repoRoot\src\Vais.Agents.Runtime.Host\Vais.Agents.Runtime.Host.csproj" `
    -c Release -o $publishDir -q

$env:VAIS_HOSTING_MODE               = "localhost"
$env:VAIS_PLUGINS_DIRECTORY          = ""
$env:VAIS_CONTAINER_PLUGINS_DIRECTORY = ""
$env:ASPNETCORE_URLS                 = "http://+:$RuntimePort"

$runtimeProc = Start-Process dotnet `
    -ArgumentList "$publishDir\Vais.Agents.Runtime.Host.dll" `
    -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput "$env:TEMP\vais-e2e-runtime.log" `
    -RedirectStandardError  "$env:TEMP\vais-e2e-runtime-err.log"

Write-Host "       runtime pid=$($runtimeProc.Id), waiting for /healthz..."
$ok = Wait-ForRuntime 90
Assert "runtime healthy" $ok
if (-not $ok) {
    Write-Host "Runtime log:" -ForegroundColor Yellow
    Get-Content "$env:TEMP\vais-e2e-runtime-err.log" -ErrorAction SilentlyContinue | Select-Object -Last 20
}

Write-Host "[3/6] Register echo-plugin via CLI (vais apply -f)"
& $VaisExe apply -f "$here\plugin.yaml" --no-build
Assert "vais apply exit 0" ($LASTEXITCODE -eq 0)

# ── Tests ─────────────────────────────────────────────────────────────────────
Write-Host "[4/6] Wait for plugin to reach Ready state"
$plugin = Wait-ForPlugin "echo-plugin" "Ready" 90
Assert "echo-plugin present"       ($null -ne $plugin)
if ($null -ne $plugin) {
    Assert "state == Ready"            ($plugin.state -eq "Ready")
    Assert "topology == standalone"    ($plugin.topology -eq "standalone")
    Assert "image == vais-echo:test"   ($plugin.image -eq "vais-echo:test")
}

Write-Host "[5/6] Push new image (hot-reload via runtime API)"
# vais plugin-push would do `docker push` first, which fails for local-only images.
# For E2E we call the runtime reload endpoint directly.
$reloadResp = Invoke-RestMethod -Method Post `
    -Uri "http://localhost:$RuntimePort/v1/plugins/echo-plugin/image" `
    -Body '{"image":"vais-echo:test-v2"}' `
    -ContentType "application/json" -ErrorAction SilentlyContinue
Assert "reload returns Success"    ($reloadResp.status -eq 0)

$plugin2 = Wait-ForPlugin "echo-plugin" "Ready" 60
Assert "plugin Ready after reload"  ($null -ne $plugin2)
if ($null -ne $plugin2) {
    Assert "image updated to test-v2"   ($plugin2.image -eq "vais-echo:test-v2")
}

Write-Host "[6/6] plugin-status --output json is machine-readable"
$statusJson = & $VaisExe plugin-status --output json 2>$null | ConvertFrom-Json
Assert "items array present"        ($null -ne $statusJson.items)
Assert "topology field present"     ($null -ne ($statusJson.items | Where-Object { $_.topology }))

# ── Teardown ─────────────────────────────────────────────────────────────────
if (-not $KeepUp) {
    Write-Host "Teardown"
    Stop-Process -Id $runtimeProc.Id -Force -ErrorAction SilentlyContinue
    # Runtime stop gives DockerContainerSupervisor time to call StopAsync on the plugin container
    Start-Sleep 3
    docker rm -f vais-plugin-echo-plugin 2>$null | Out-Null
    Remove-Item $tmpConfig -ErrorAction SilentlyContinue
}

# ── Summary ───────────────────────────────────────────────────────────────────
Write-Host ""
if ($failed -eq 0) {
    Write-Host "=== Docker suite PASSED ($passed/$($passed+$failed)) ===" -ForegroundColor Green
    exit 0
} else {
    Write-Host "=== Docker suite FAILED ($failed failed, $passed passed) ===" -ForegroundColor Red
    exit 1
}
