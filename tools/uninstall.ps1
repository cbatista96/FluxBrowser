<#
.SYNOPSIS
  Removes what install.ps1 created. Your profile (favourites, history, settings) is kept unless -RemoveData is given.
#>
param(
    [switch]$RemoveData
)

$ErrorActionPreference = 'Stop'
$directory = $PSScriptRoot.TrimEnd('\')
$exe = Join-Path $directory 'MyOwnBrowser.exe'

$running = Get-Process MyOwnBrowser -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($directory, 'OrdinalIgnoreCase') }
if ($running) { throw 'Cierra My Own Browser antes de desinstalar.' }

if (Test-Path $exe) { Start-Process $exe -ArgumentList '--unregister' -Wait | Out-Null }
Remove-Item (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\My Own Browser.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MyOwnBrowser' -Recurse -Force -ErrorAction SilentlyContinue
if ($RemoveData) { Remove-Item (Join-Path $env:LOCALAPPDATA 'MyOwnBrowser') -Recurse -Force -ErrorAction SilentlyContinue }

# This script lives inside the folder being removed: delete it from a detached process once we have exited.
Start-Process cmd.exe -ArgumentList "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q `"$directory`"" -WindowStyle Hidden
Write-Host 'My Own Browser se desinstaló.' -ForegroundColor Green
if (-not $RemoveData) { Write-Host 'Tus datos se conservaron en %LOCALAPPDATA%\MyOwnBrowser (usa -RemoveData para borrarlos).' }
