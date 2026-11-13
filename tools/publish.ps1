<#
.SYNOPSIS
  Builds a portable release: runs the tests, publishes self-contained ReadyToRun, trims unused locales,
  smoke-tests the result and writes a zip plus its SHA-256 under artifacts\.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\publish.ps1
  powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -SkipSmokeTest
#>
param(
    [string]$Configuration = 'Release',
    [switch]$SkipTests,
    [switch]$SkipSmokeTest,
    [switch]$SkipZip
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'FluxBrowser\FluxBrowser.csproj'
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'

function Step([string]$text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Run([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe terminó con código $LASTEXITCODE" }
}

$version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'No se encontró <Version> en el .csproj.' }
Write-Host "Flux Browser $version ($Configuration, win-x64)"

if (-not $SkipTests) {
    Step 'Pruebas automáticas'
    Run 'dotnet' @('test', (Join-Path $root 'FluxBrowser.Tests\FluxBrowser.Tests.csproj'), '-c', $Configuration, '--nologo', '-v:m')
}

Step 'Publicando (autocontenido, ReadyToRun)'
if (Test-Path -LiteralPath $publish) {
    $resolved = (Resolve-Path -LiteralPath $publish).Path
    if ($resolved -ne $publish -or (Get-Item -LiteralPath $publish).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
        throw "La ruta de publicación no es una carpeta directa de artifacts: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
Run 'dotnet' @('publish', $project, '-c', $Configuration, '-p:Platform=x64', '-r', 'win-x64', '--self-contained', 'true', '-o', $publish, '--nologo', '-v:m')

Step 'Eliminando idiomas que la interfaz no usa'
# CEF falls back to en-US when a locale is missing; the UI itself is Spanish. Keep English, Spanish and Latin-American Spanish.
$keepPak = 'en-US.pak', 'es.pak', 'es-419.pak'
$locales = Join-Path $publish 'locales'
if (Test-Path $locales) {
    Get-ChildItem $locales -Filter *.pak | Where-Object { $keepPak -notcontains $_.Name } | Remove-Item -Force
}
$keepMui = 'en-us', 'es-es', 'es-mx'
Get-ChildItem $publish -Directory | Where-Object {
    (Test-Path (Join-Path $_.FullName 'Microsoft.ui.xaml.dll.mui')) -and ($keepMui -notcontains $_.Name.ToLowerInvariant())
} | Remove-Item -Recurse -Force
Copy-Item (Join-Path $PSScriptRoot 'install.ps1'), (Join-Path $PSScriptRoot 'uninstall.ps1') $publish
Copy-Item (Join-Path $root 'docs/third-party') $publish -Recurse
$size = (Get-ChildItem $publish -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Tamaño publicado: {0:N0} MB" -f $size)

if (-not $SkipSmokeTest) {
    # WebView2 is the default engine; CEF is the fallback for machines without the WebView2 runtime. Both must start.
    foreach ($engine in 'webview2', 'cef') {
        Step "Prueba de humo del ejecutable publicado (motor: $engine)"
        # A throw-away profile makes this copy an independent instance, so it does not collide with a browser you already have open.
        $profile = Join-Path ([IO.Path]::GetTempPath()) ("mob-smoke-" + [guid]::NewGuid().ToString('N'))
        $previousProfile = $env:FLUXBROWSER_PROFILE
        $previousEngine = $env:FLUXBROWSER_ENGINE
        $env:FLUXBROWSER_PROFILE = $profile
        $env:FLUXBROWSER_ENGINE = $engine
        $process = $null
        try {
            $process = Start-Process (Join-Path $publish 'FluxBrowser.exe') -PassThru
            Start-Sleep -Seconds 12
            if ($process.HasExited) { throw "La aplicación se cerró durante el arranque con $engine (código $($process.ExitCode))." }
            $process.Refresh()
            if ($process.MainWindowTitle -notlike '*Flux Browser*') { throw "No apareció la ventana principal con $engine (título: '$($process.MainWindowTitle)')." }
            $log = Join-Path $profile 'logs\app.log'
            if ((Test-Path $log) -and (Select-String -Path $log -Pattern '\[ERROR\]' -Quiet)) { throw "El registro contiene errores con ${engine}: $log" }
            $expected = if ($engine -eq 'webview2') { 'WebView2 \(con sandbox\)' } else { 'CEF \(sin sandbox\)' }
            if (-not (Select-String -Path $log -Pattern $expected -Quiet)) { throw "El motor elegido no es $engine (consulta $log)." }
            Write-Host "OK ($engine): la ventana principal se abrió y no hay errores registrados." -ForegroundColor Green
        }
        finally {
            if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
            Start-Sleep -Seconds 2
            $env:FLUXBROWSER_PROFILE = $previousProfile
            $env:FLUXBROWSER_ENGINE = $previousEngine
            if (Test-Path $profile) { Remove-Item $profile -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }
}

if (-not $SkipZip) {
    Step 'Empaquetando'
    $zip = Join-Path $artifacts "FluxBrowser-$version-win-x64.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($publish, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -Path "$zip.sha256" -Value "$hash  $(Split-Path $zip -Leaf)" -Encoding ascii
    Write-Host ("{0} ({1:N0} MB)`nSHA-256: {2}" -f $zip, ((Get-Item $zip).Length / 1MB), $hash)
}
