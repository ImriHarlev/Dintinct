$dest = "C:\tools\ffmpeg"
Invoke-WebRequest "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip" -OutFile "$env:TEMP\ffmpeg.zip"
Expand-Archive "$env:TEMP\ffmpeg.zip" -DestinationPath "$env:TEMP\ffmpeg-extract" -Force
$extracted = Get-ChildItem "$env:TEMP\ffmpeg-extract" -Directory | Select-Object -First 1
Move-Item $extracted.FullName $dest -Force
Rename-Item "$dest\$($extracted.Name)" "bin" -ErrorAction SilentlyContinue