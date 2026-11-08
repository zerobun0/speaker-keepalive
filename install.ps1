# Builds SpeakerKeepAlive from source with the C# compiler that ships with Windows,
# installs it to %LOCALAPPDATA%\SpeakerKeepAlive, adds it to Startup and starts it.
# No admin rights needed.
$ErrorActionPreference = 'Stop'
$src     = Join-Path $PSScriptRoot 'src\SpeakerKeepAlive.cs'
$dest    = Join-Path $env:LOCALAPPDATA 'SpeakerKeepAlive'
$exe     = Join-Path $dest 'SpeakerKeepAlive.exe'
$startup = [Environment]::GetFolderPath('Startup')
$csc     = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw "C# compiler not found (.NET Framework 4.x is part of Windows 10/11)." }

Get-Process SpeakerKeepAlive -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Force $dest | Out-Null

& $csc /nologo /optimize+ /target:winexe "/out:$exe" $src
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$ini = Join-Path $dest 'SpeakerKeepAlive.ini'
if (-not (Test-Path $ini)) { Copy-Item (Join-Path $PSScriptRoot 'SpeakerKeepAlive.ini') $ini }

# SoundKeeper and similar tools play their keep-alive *before* Windows volume, so at low volume they
# stop reaching the speakers. Running both just stacks streams; disable SoundKeeper's autostart.
$sk = Get-ChildItem $startup -Filter 'SoundKeeper*.lnk' -ErrorAction SilentlyContinue
if ($sk) {
    $bak = Join-Path $dest 'disabled-startup'
    New-Item -ItemType Directory -Force $bak | Out-Null
    $sk | Move-Item -Destination $bak -Force
    Get-Process SoundKeeper* -ErrorAction SilentlyContinue | Stop-Process -Force
    Write-Host "Disabled SoundKeeper autostart (shortcut moved to $bak)."
}

$sh = New-Object -ComObject WScript.Shell
$lnk = $sh.CreateShortcut((Join-Path $startup 'SpeakerKeepAlive.lnk'))
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = $dest
$lnk.Description = 'Keeps speakers awake at low volume'
$lnk.Save()

Start-Process $exe -WorkingDirectory $dest
Start-Sleep 2
Write-Host "Installed to $dest and running. Log: $dest\SpeakerKeepAlive.log"
Write-Host "Diagnostics any time: & '$exe' --diag ; then open $dest\SpeakerKeepAlive-diag.txt"
