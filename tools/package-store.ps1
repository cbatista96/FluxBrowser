[CmdletBinding(DefaultParameterSetName='Store')]
param(
    [Parameter(Mandatory, ParameterSetName='Preview')]
    [switch]$LocalPreview,
    [Parameter(Mandatory, ParameterSetName='Store')]
    [string]$IdentityName,
    [Parameter(Mandatory, ParameterSetName='Store')]
    [string]$Publisher,
    [Parameter(Mandatory, ParameterSetName='Store')]
    [string]$PublisherDisplayName,
    [string]$MakeAppx
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'
$project = Join-Path $root 'MyOwnBrowser/MyOwnBrowser.csproj'
$version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

if (-not (Test-Path (Join-Path $publish 'FluxBrowser.exe'))) {
    throw 'Genera artifacts/publish con tools/publish.ps1.'
}
if ((Get-Item (Join-Path $publish 'FluxBrowser.exe')).VersionInfo.FileVersion -ne "$version.0") {
    throw 'La versión del binario publicado no coincide con el proyecto.'
}
if (-not $MakeAppx) {
    $kit = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
    $MakeAppx = Get-ChildItem $kit -Directory | Where-Object Name -Match '^10\.0\.\d+\.\d+$' |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName 'x64/MakeAppx.exe' } |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $MakeAppx) { throw 'Instala Windows SDK o indica -MakeAppx.' }

[xml]$manifest = Get-Content (Join-Path $root 'store/AppxManifest.xml') -Raw
$manifest.Package.Identity.Version = "$version.0"
if (-not $LocalPreview) {
    $manifest.Package.Identity.Name = $IdentityName
    $manifest.Package.Identity.Publisher = $Publisher
    $manifest.Package.Properties.PublisherDisplayName = $PublisherDisplayName
}
$channel = if ($LocalPreview) { 'store-preview' } else { 'store' }
$output = Join-Path $artifacts "FluxBrowser-$version-$channel-x64.msix"
$staging = Join-Path $artifacts ('msix-' + [guid]::NewGuid().ToString('N'))
$temporaryPackage = "$staging.msix"
New-Item -ItemType Directory -Path $staging | Out-Null
try {
    Get-ChildItem -LiteralPath $publish | Where-Object Extension -NotIn '.ps1','.pdb' |
        Copy-Item -Destination $staging -Recurse
    Copy-Item (Join-Path $root 'store/Assets/*.png') (Join-Path $staging 'Assets')
    $manifest.Save((Join-Path $staging 'AppxManifest.xml'))
    $packLog = & $MakeAppx pack /d $staging /p $temporaryPackage
    if ($LASTEXITCODE -ne 0) { throw (($packLog | Select-Object -Last 15) -join [Environment]::NewLine) }
    Move-Item -LiteralPath $temporaryPackage -Destination $output -Force
    Get-FileHash -LiteralPath $output -Algorithm SHA256
} finally {
    if (Test-Path -LiteralPath $temporaryPackage) { Remove-Item -LiteralPath $temporaryPackage -Force }
    $resolved = (Resolve-Path -LiteralPath $staging).Path
    if ([IO.Path]::GetDirectoryName($resolved) -eq (Resolve-Path -LiteralPath $artifacts).Path) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

