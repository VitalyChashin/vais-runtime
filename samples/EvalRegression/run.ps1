#!/usr/bin/env pwsh
# Apply manifests, run the suite, and print results.
# Prerequisites: vais CLI installed and pointed at a running runtime.
# Set OPENAI_API_KEY on the runtime side before running.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Write-Host "==> Applying agent manifest..."
vais apply -f "$PSScriptRoot/agent.yaml"

Write-Host "==> Applying eval suite manifest..."
vais apply -f "$PSScriptRoot/eval-suite.yaml"

Write-Host "==> Starting eval run (--wait streams live progress)..."
vais eval run support-bot-regression --wait

Write-Host "==> Fetching run id of the most recent run..."
$runId = (vais eval list --suite support-bot-regression --limit 1 -o json | ConvertFrom-Json)[0].evalRunId
Write-Host "    run id: $runId"

Write-Host "==> Results table:"
vais eval results $runId

Write-Host "==> JUnit XML (piped to eval-results.xml):"
vais eval results $runId -o junit | Out-File -FilePath "$PSScriptRoot/eval-results.xml" -Encoding UTF8
Write-Host "    written to eval-results.xml"
