$ErrorActionPreference = "Stop"

# Configuration
$jobId = "test-$(Get-Date -Format 'HHmm')"
$sourcePackage = "\network-a\incoming\pres.ppt"
$targetPath = "\network-b\output"
$responsePath = "\network-b\responses"

Write-Host "--- Starting End-to-End Test: Job $jobId ---" -ForegroundColor Cyan

# 1. Submit Ingestion Request
Write-Host "`n[1/4] Submitting Ingestion Request via HTTP API..." -ForegroundColor Yellow
$body = @{
    callingSystemId   = "AUTO-TEST"
    callingSystemName = "PowerShell Runner"
    externalId        = $jobId
    sourcePath        = $sourcePackage
    targetPath        = $targetPath
    targetNetwork     = "NetworkB"
    answerType        = "FILE_SYSTEM"
    answerLocation    = $responsePath
} | ConvertTo-Json

$response = Invoke-RestMethod -Method Post -Uri "http://localhost:5161/api/v1/ingestion" `
    -ContentType "application/json" `
    -Body $body

Write-Host "  Response Received: $($response.status) (Job: $($response.jobId))" -ForegroundColor Gray

# 2. Wait for Decomposition
Write-Host "`n[2/4] Waiting 30 seconds for Network A to process and stage files..." -ForegroundColor Yellow
Start-Sleep -Seconds 30

# 3. Simulate Proxy (File Copy)
Write-Host "`n[3/4] Simulating Proxy: Copying outbox to inbox..." -ForegroundColor Yellow

New-Item -ItemType Directory -Force -Path "\network-b\inbox\data\" | Out-Null
New-Item -ItemType Directory -Force -Path "\network-b\inbox\manifest\" | Out-Null

# Copy Data
Copy-Item -Path "\network-a\outbox\data\*" `
    -Destination "\network-b\inbox\data\" -Recurse -Force
# Copy Metadata (Manifest)
Copy-Item -Path "\network-a\outbox\manifest\*" `
    -Destination "\network-b\inbox\manifest\" -Recurse -Force
Write-Host "  Files moved to Network B." -ForegroundColor Gray

# 4. Notify Network B via RabbitMQ
Write-Host "`n[4/4] Sending arrival signals to RabbitMQ..." -ForegroundColor Yellow

$rmqCredential = New-Object System.Management.Automation.PSCredential(
    "guest",
    (ConvertTo-SecureString "guest" -AsPlainText -Force)
)

# Function to publish to RabbitMQ via Management API
function Publish-ProxySignal($filePath, $credential) {
    $payload = @{
        properties = @{ delivery_mode = 2 }
        routing_key = "file.arrived"
        payload = (@{
            filePath  = $filePath
            timestamp = (Get-Date -Format "yyyy-MM-ddTHH:mm:ssZ")
        } | ConvertTo-Json)
        payload_encoding = "string"
    } | ConvertTo-Json

    # Added -AllowUnencryptedAuthentication to bypass the security block for localhost
    Invoke-RestMethod -Method Post -Uri "http://localhost:15672/api/exchanges/%2f/proxy.events/publish" `
        -Authentication Basic `
        -Credential $credential `
        -AllowUnencryptedAuthentication `
        -ContentType "application/json" `
        -Body $payload | Out-Null
}

# Find the specific chunk and manifest for this job
$jobIdStr = $response.jobId
$chunks = Get-ChildItem "\network-b\inbox\data\" -Filter "${jobIdStr}_chunk_*" -File
$manifest = "\network-b\inbox\manifest\${jobIdStr}_manifest.json"

foreach ($chunk in $chunks) {
    Write-Host "  Signaling chunk: $($chunk.Name)" -ForegroundColor Gray
    Publish-ProxySignal $chunk.FullName $rmqCredential
}

Write-Host "  Signaling manifest: manifest.json" -ForegroundColor Gray
Publish-ProxySignal $manifest $rmqCredential

Write-Host "`n--- Test Execution Submitted ---" -ForegroundColor Green
Write-Host "Monitor progress at http://localhost:8233"
Write-Host "Check result at: $targetPath"