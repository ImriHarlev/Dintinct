$ErrorActionPreference = "Stop"

# Configuration
$externalId = "externalId-$([guid]::NewGuid().ToString().Substring(0,8))"
$sourcePackage = "\network-a\incoming\lol"
$targetPath = "\network-b\output"
$responsePath = "\network-b\responses"

Write-Host "--- Starting End-to-End Test: Job $externalId ---" -ForegroundColor Cyan

# 1. Submit Ingestion Request
Write-Host "`n[1/4] Submitting Ingestion Request via HTTP API..." -ForegroundColor Yellow
$body = @{
    callingSystemId   = "Imri-callingSystemId"
    callingSystemName = "Imri-callingSystemName"
    externalId        = $externalId
    sourcePath        = $sourcePackage
    targetPath        = "$($targetPath)\$($externalId)"
    targetNetwork     = "NetworkB"
    answerType        = "FILE_SYSTEM"
    answerLocation    = $responsePath
} | ConvertTo-Json

$response = Invoke-RestMethod -Method Post -Uri "http://localhost:5161/api/v1/ingestion" `
    -ContentType "application/json" `
    -Body $body

Write-Host "  Response Received: $($response.status) (Job: $($response.jobId))" -ForegroundColor Gray