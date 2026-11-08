$dest    = Join-Path $env:LOCALAPPDATA 'SpeakerKeepAlive'
$startup = [Environment]::GetFolderPath('Startup')
Get-Process SpeakerKeepAlive -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item (Join-Path $startup 'SpeakerKeepAlive.lnk') -ErrorAction SilentlyContinue
# Put back anything install.ps1 disabled (e.g. SoundKeeper's autostart)
$bak = Join-Path $dest 'disabled-startup'
if (Test-Path $bak) { Get-ChildItem $bak | Move-Item -Destination $startup -Force }
Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
Write-Host 'SpeakerKeepAlive removed.'
