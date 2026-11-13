<#
.SYNOPSIS
  Per-user installer (no administrator rights): copies the app, adds a Start menu shortcut, registers it as a web browser
  in Windows and creates an "Installed apps" entry. Included at the root of the release zip, next to MyOwnBrowser.exe.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File install.ps1
  powershell -ExecutionPolicy Bypass -File install.ps1 -InstallDirectory D:\Apps\MyOwnBrowser
#>
param(
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\MyOwnBrowser'),
    [switch]$NoShortcut,
    [switch]$NoRegister
)

$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot
$exeName = 'MyOwnBrowser.exe'
if (-not (Test-Path (Join-Path $source $exeName))) { throw "No se encontró $exeName junto al instalador. Ejecuta install.ps1 desde la carpeta descomprimida." }
$InstallDirectory = [IO.Path]::GetFullPath($InstallDirectory)
if ($InstallDirectory.TrimEnd('\') -eq $source.TrimEnd('\')) { throw 'El destino no puede ser la carpeta de origen.' }

$running = Get-Process MyOwnBrowser -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($InstallDirectory, 'OrdinalIgnoreCase') }
if ($running) { throw 'Cierra My Own Browser antes de instalar o actualizar.' }

Write-Host "Instalando en $InstallDirectory ..."
New-Item -ItemType Directory -Force $InstallDirectory | Out-Null
& robocopy $source $InstallDirectory /E /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy falló con código $LASTEXITCODE" }

$exe = Join-Path $InstallDirectory $exeName
$version = (Get-Item $exe).VersionInfo.ProductVersion
if (-not $version) { $version = '0.0.0' }

if (-not $NoShortcut) {
    $shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\My Own Browser.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($shortcut)
    $link.TargetPath = $exe
    $link.WorkingDirectory = $InstallDirectory
    $link.IconLocation = "$exe,0"
    $link.Description = 'My Own Browser'
    $link.Save()
}

if (-not $NoRegister) {
    $registration = Start-Process $exe -ArgumentList '--register' -Wait -PassThru
    if ($registration.ExitCode -ne 0) { Write-Warning 'No se pudo registrar el navegador en Windows (consulta logs\app.log del perfil).' }
}

$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MyOwnBrowser'
New-Item -Path $key -Force | Out-Null
$size = [int]((Get-ChildItem $InstallDirectory -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
$properties = @{
    DisplayName     = 'My Own Browser'
    DisplayVersion  = $version
    Publisher       = 'My Own Browser'
    InstallLocation = $InstallDirectory
    DisplayIcon     = "$exe,0"
    UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $InstallDirectory 'uninstall.ps1')`""
    EstimatedSize   = $size
}
foreach ($name in $properties.Keys) { Set-ItemProperty -Path $key -Name $name -Value $properties[$name] }
Set-ItemProperty -Path $key -Name NoModify -Value 1 -Type DWord
Set-ItemProperty -Path $key -Name NoRepair -Value 1 -Type DWord

Write-Host "Instalado: My Own Browser $version" -ForegroundColor Green
Write-Host 'Para usarlo como navegador predeterminado: Configuración de Windows > Aplicaciones > Aplicaciones predeterminadas.'
