# Stop Docker containers
Write-Host "Stopping Docker containers..." -ForegroundColor Yellow
docker stop airgap_temporal_ui
docker stop airgap_temporal_db
docker stop airgap_temporal
docker stop airgap_rabbitmq

# Remove Docker containers
Write-Host "Removing Docker containers..." -ForegroundColor Yellow
docker rm airgap_temporal_ui
docker rm airgap_temporal_db
docker rm airgap_temporal
docker rm airgap_rabbitmq

Write-Host "Cleanup completed!" -ForegroundColor Green
