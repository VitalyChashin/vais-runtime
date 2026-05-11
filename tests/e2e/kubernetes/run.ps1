# E2E Kubernetes Suite — tests container plugin Kubernetes topology.
#
# Uses Docker Desktop Kubernetes (shared Docker daemon — imagePullPolicy: Never).
# The runtime runs as a K8s Deployment (Helm); the echo plugin is deployed and
# registered with the running runtime via `vais plugin-deploy` (validates P11).
#
# Prerequisites:
#   - Docker Desktop with Kubernetes enabled
#   - helm on PATH
#   - kubectl on PATH (context pointing at Docker Desktop cluster)
#   - vais CLI on PATH (or set $VaisExe)
#   - echo plugin image built:
#       docker build -t vais-echo:test tests/e2e/shared/echo-plugin/
#       docker tag  vais-echo:test vais-echo:test-v2

param(
    [string]$Namespace  = "vais-e2e",
    [switch]$KeepUp,
    [string]$VaisExe    = "vais",
    [int]$RuntimePort   = 18081    # local port for kubectl port-forward
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$here     = $PSScriptRoot
$e2eRoot  = Resolve-Path "$here\.."           # tests/e2e/
$repoRoot = Resolve-Path "$here\..\..\.."     # agentic/
$chart   = "$repoRoot\deploy\helm\vais-agents-runtime"
$passed  = 0
$failed  = 0
$fwdProc = $null

function Assert([string]$label, [bool]$condition) {
    if ($condition) {
        Write-Host "  PASS  $label" -ForegroundColor Green
        $script:passed++
    } else {
        Write-Host "  FAIL  $label" -ForegroundColor Red
        $script:failed++
    }
}

function Wait-ForPlugin([string]$name, [string]$state = "Ready", [int]$timeoutSec = 120) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    do {
        try {
            $json = & $VaisExe plugin-status --output json 2>$null | ConvertFrom-Json
            $p    = $json.items | Where-Object { $_.name -eq $name }
            if ($p -and $p.state -eq $state) { return $p }
        } catch { }
        Start-Sleep 5
    } until ((Get-Date) -gt $deadline)
    return $null
}

# ── Setup ─────────────────────────────────────────────────────────────────────
Write-Host "`n=== E2E Kubernetes Suite ===" -ForegroundColor Cyan

Write-Host "[1/7] Build images (imagePullPolicy: Never — Docker Desktop shared daemon)"
docker build -t vais-echo:test  "$e2eRoot\shared\echo-plugin" -q | Out-Null
docker tag vais-echo:test vais-echo:test-v2
docker build -t vais-agents-runtime:e2e "$repoRoot" -f "$here\Dockerfile.e2e" -q | Out-Null

Write-Host "[2/7] Create namespace + Helm install runtime"
kubectl create namespace $Namespace --dry-run=client -o yaml | kubectl apply -f - | Out-Null
helm upgrade --install vais-runtime $chart `
    --namespace $Namespace --create-namespace `
    --set image.repository=vais-agents-runtime `
    --set image.tag=e2e `
    --set image.pullPolicy=Never `
    --set hosting.mode=localhost `
    --set rbac.create=true `
    --set rbac.pluginSupervision=true `
    --wait --timeout 120s

Write-Host "[3/7] Port-forward runtime service → localhost:$RuntimePort"
$fwdProc = Start-Process kubectl `
    -ArgumentList "port-forward -n $Namespace svc/vais-runtime-vais-agents-runtime $RuntimePort`:8080" `
    -PassThru -WindowStyle Hidden
Start-Sleep 4   # give the forwarder time to bind

# Write temp vais config
$tmpConfig = [System.IO.Path]::GetTempFileName() -replace '\.tmp$', '.yaml'
@"
apiVersion: vais.io/v1
kind: Config
currentContext: e2e-k8s
clusters:
  - name: e2e-k8s
    server: http://localhost:$RuntimePort
contexts:
  - name: e2e-k8s
    cluster: e2e-k8s
    user: ""
users: []
"@ | Set-Content $tmpConfig
$env:VAIS_CONFIG = $tmpConfig

Write-Host "[4/7] Deploy + register echo plugin (Helm + control plane)"
& $VaisExe plugin-deploy echo-plugin `
    --image vais-echo:test `
    --namespace $Namespace `
    --image-pull-policy Never `
    --replicas 1

# ── Tests ─────────────────────────────────────────────────────────────────────
Write-Host "[5/7] Wait for echo-plugin to reach Ready"
$plugin = Wait-ForPlugin "echo-plugin" "Ready" 120
Assert "echo-plugin present"              ($null -ne $plugin)
if ($null -ne $plugin) {
    Assert "state == Ready"                   ($plugin.state -eq "Ready")
    Assert "topology == kubernetes"           ($plugin.topology -eq "kubernetes")
    Assert "deploymentName set"               ($plugin.kubernetesDeploymentName -eq "echo-plugin")
    Assert "namespace set"                    ($plugin.kubernetesNamespace -eq $Namespace)
}

Write-Host "[6/7] Push new image → expect RolloutStarted (HTTP 202)"
# vais plugin-push would do `docker push` first, which fails for local-only images.
# For E2E we call the runtime reload endpoint directly.
$reloadResp = Invoke-RestMethod -Method Post `
    -Uri "http://localhost:$RuntimePort/v1/plugins/echo-plugin/image" `
    -Body '{"image":"vais-echo:test-v2"}' `
    -ContentType "application/json" -ErrorAction SilentlyContinue
Assert "reload returns RolloutStarted"    ($reloadResp.status -eq 5)

Write-Host "[7/7] plugin-status JSON fields"
$statusJson = & $VaisExe plugin-status --output json 2>$null | ConvertFrom-Json
$ep = $statusJson.items | Where-Object { $_.name -eq "echo-plugin" }
Assert "topology field in JSON"           ($ep.topology -eq "kubernetes")
Assert "kubernetesDeploymentName in JSON" ($ep.kubernetesDeploymentName -eq "echo-plugin")

# ── Teardown ─────────────────────────────────────────────────────────────────
if (-not $KeepUp) {
    Write-Host "Teardown"
    if ($fwdProc) { Stop-Process -Id $fwdProc.Id -Force -ErrorAction SilentlyContinue }
    helm uninstall vais-runtime --namespace $Namespace --wait 2>&1 | Out-Null
    kubectl delete namespace $Namespace --ignore-not-found 2>&1 | Out-Null
    Remove-Item $tmpConfig -ErrorAction SilentlyContinue
}

# ── Summary ───────────────────────────────────────────────────────────────────
Write-Host ""
if ($failed -eq 0) {
    Write-Host "=== Kubernetes suite PASSED ($passed/$($passed+$failed)) ===" -ForegroundColor Green
    exit 0
} else {
    Write-Host "=== Kubernetes suite FAILED ($failed failed, $passed passed) ===" -ForegroundColor Red
    exit 1
}
