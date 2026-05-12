$ErrorActionPreference = "Stop"

# Configuration
$imageFolder = "C:\network-a\incoming\videoAudio"
$targetPath = "\network-b\output"
$responsePath = "\network-b\responses"

# Get all files from the Image folder
$files = Get-ChildItem -Path $imageFolder -File

if ($files.Count -eq 0) {
	Write-Host "No files found in $imageFolder" -ForegroundColor Red
	exit
}

Write-Host "Found $($files.Count) file(s) to process" -ForegroundColor Cyan
Write-Host "================================================`n" -ForegroundColor Cyan

# Loop through each file
foreach ($file in $files) {
	$externalId = "externalId-$([guid]::NewGuid().ToString().Substring(0,8))"
	$sourcePackage = "\network-a\incoming\Image\$($file.Name)"

	Write-Host "--- Processing: $($file.Name) (Job: $externalId) ---" -ForegroundColor Green

	# Submit Ingestion Request
	Write-Host "Submitting Ingestion Request via HTTP API..." -ForegroundColor Yellow

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

	try {
		$response = Invoke-RestMethod -Method Post -Uri "http://localhost:5161/api/v1/ingestion" `
			-ContentType "application/json" `
			-Body $body

		Write-Host "  Response: $($response.status) (Job: $($response.jobId))" -ForegroundColor Gray
		Write-Host "  File: $($file.Name) submitted successfully!`n" -ForegroundColor Green
	}
	catch {
		Write-Host "  Error processing $($file.Name): $_" -ForegroundColor Red
		Write-Host ""
	}

	# Optional: Add a delay between requests (uncomment if needed)
	# Start-Sleep -Seconds 2
}

Write-Host "================================================" -ForegroundColor Cyan
Write-Host "All files processed!" -ForegroundColor Cyan
