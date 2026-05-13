# E2E Docker Suite — tests container plugin Docker-standalone topology.
#
# Two modes:
#
#   Default (legacy)          — runtime runs directly on the host so
#                               DockerContainerSupervisor can reach the Docker
#                               daemon and create the echo plugin container without
#                               Docker-in-Docker complexity.
#
#   -UseInternalNetwork       — runtime runs as a container on a per-run internal
#                               Docker network alongside the plugin container.
#                               The plugin has no published host port; the runtime
#                               reaches it via Docker embedded DNS.
#                               Requires vais-research-pipeline:local to be
#                               pre-built (run local-dev/dev.ps1 start first).
#
# Prerequisites (both modes):
#   - dotnet 9 SDK on PATH
#   - docker on PATH and daemon running
#   - vais CLI on PATH (or set $VaisExe)
#
# Additional prerequisite (-UseInternalNetwork):
#   - vais-research-pipeline:local image already built

param(
    [switch]$KeepUp,              # skip teardown (leave runtime running for debugging)
    [switch]$UseInternalNetwork,  # Phase 2 egress isolation mode
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
if ($UseInternalNetwork) {
    Write-Host "    Mode: internal-network (Phase 2 isolation)" -ForegroundColor DarkCyan
} else {
    Write-Host "    Mode: legacy host-runtime" -ForegroundColor DarkGray
}

# Pre-run cleanup: remove stale containers from prior aborted runs.
try { docker rm -f vais-plugin-echo-plugin 2>$null | Out-Null } catch {}

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

# ── Start runtime (mode-dependent) ────────────────────────────────────────────

if ($UseInternalNetwork) {
    # Verify the runtime image is available.
    $imgCheck = docker image inspect vais-research-pipeline:local 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "Image vais-research-pipeline:local not found. Run 'local-dev/dev.ps1 start' first."
    }

    # Per-run network name avoids cross-run collision when tests run in parallel.
    $pluginNet = "vais-e2e-plugin-net"

    Write-Host "[2/6] Create internal network '$pluginNet' and start runtime container"
    # Idempotent — no error if already exists (e.g. from a prior -KeepUp run).
    docker network create --internal $pluginNet 2>$null; $true
    try { docker rm -f vais-e2e-runtime 2>$null | Out-Null } catch {}

    # Docker socket path: /var/run/docker.sock works on Linux and Docker Desktop
    # (macOS/Windows) for Linux-container workloads. Docker Desktop translates
    # this transparently via its VM socket proxy.
    docker run -d `
        --name vais-e2e-runtime `
        --network $pluginNet `
        -v /var/run/docker.sock:/var/run/docker.sock `
        -e VAIS_HOSTING_MODE=localhost `
        -e "ASPNETCORE_URLS=http://0.0.0.0:$RuntimePort" `
        -e VAIS_CONTAINER_PLUGINS_DIRECTORY=/tmp/vais-e2e-plugins `
        -e VAIS_DOCKER_PLUGIN_NETWORK=$pluginNet `
        -p "127.0.0.1:${RuntimePort}:${RuntimePort}" `
        vais-research-pipeline:local | Out-Null

    Write-Host "       runtime container starting, waiting for /healthz..."
    $ok = Wait-ForRuntime 120
    Assert "runtime healthy" $ok
    if (-not $ok) {
        Write-Host "Runtime logs:" -ForegroundColor Yellow
        docker logs vais-e2e-runtime 2>&1 | Select-Object -Last 20
    }
} else {
    Write-Host "[2/6] Publish + start runtime (no filesystem plugins)"
    $publishDir = Join-Path $env:TEMP "vais-e2e-runtime"
    New-Item -ItemType Directory -Force $publishDir | Out-Null
    dotnet publish "$repoRoot\src\Vais.Agents.Runtime.Host\Vais.Agents.Runtime.Host.csproj" `
        -c Release -o $publishDir -q

    $env:VAIS_HOSTING_MODE               = "localhost"
    $env:VAIS_PLUGINS_DIRECTORY          = ""
    # Non-empty so AddContainerPlugins registers IContainerPluginLifecycleManager; path need not exist.
    $env:VAIS_CONTAINER_PLUGINS_DIRECTORY = "$env:TEMP\vais-e2e-plugins"
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

if ($UseInternalNetwork) {
    # Verify the plugin container has no published host ports.
    $portBindingsRaw = docker inspect vais-plugin-echo-plugin `
        --format '{{json .HostConfig.PortBindings}}' 2>$null
    $portBindings = $portBindingsRaw | ConvertFrom-Json
    $hasNoPorts = ($null -eq $portBindings) -or
                  (($portBindings | Get-Member -MemberType NoteProperty -ErrorAction SilentlyContinue).Count -eq 0)
    Assert "plugin has no published host ports (internal-network mode)" $hasNoPorts

    # Verify the plugin is on the internal network.
    $networks = docker inspect vais-plugin-echo-plugin `
        --format '{{json .NetworkSettings.Networks}}' 2>$null | ConvertFrom-Json
    Assert "plugin is on internal network '$pluginNet'" `
        ($null -ne ($networks | Get-Member -Name $pluginNet -MemberType NoteProperty -ErrorAction SilentlyContinue))
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
    if ($UseInternalNetwork) {
        docker rm -f vais-e2e-runtime 2>$null | Out-Null
        # Plugin container is cleaned up by DockerContainerSupervisor on runtime shutdown.
        # Force-remove in case the runtime didn't get a chance to clean up.
        docker rm -f vais-plugin-echo-plugin 2>$null | Out-Null
        docker network rm $pluginNet 2>$null | Out-Null
    } else {
        Stop-Process -Id $runtimeProc.Id -Force -ErrorAction SilentlyContinue
        # Runtime stop gives DockerContainerSupervisor time to call StopAsync on the plugin container
        Start-Sleep 3
        docker rm -f vais-plugin-echo-plugin 2>$null | Out-Null
    }
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
