$ErrorActionPreference = "Stop"

# Configuration
$jobId = "test-$(Get-Date -Format 'HHmm')"
$sourceFile = "C:\airgap\network-a\incoming\sample.txt"
$targetFile = "C:\airgap\network-b\output\sample-$jobId.txt"
$responseFile = "C:\airgap\network-a\responses\$jobId.json"

Write-Host "--- Starting End-to-End Test: Job $jobId ---" -ForegroundColor Cyan

# 1. Submit Ingestion Request
Write-Host "`n[1/4] Submitting Ingestion Request via HTTP API..." -ForegroundColor Yellow
$body = @{
    callingSystemId   = "AUTO-TEST"
    callingSystemName = "PowerShell Runner"
    externalId        = $jobId
    sourcePath        = $sourceFile
    targetPath        = $targetFile
    targetNetwork     = "NetworkB"
    answerType        = "FILE_SYSTEM"
    answerLocation    = $responseFile
} | ConvertTo-Json

$response = Invoke-RestMethod -Method Post -Uri "http://localhost:5161/api/v1/ingestion" `
    -ContentType "application/json" `
    -Body $body

Write-Host "  Response Received: $($response.status) (Job: $($response.jobId))" -ForegroundColor Gray