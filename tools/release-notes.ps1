param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$repo = 'https://github.com/cbatista96/FluxBrowser'
$raw = "https://raw.githubusercontent.com/cbatista96/FluxBrowser/v$Version/"
$artifacts = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
$sections = foreach ($language in @(
    @{ File = 'CHANGELOG.en.md'; Code = 'en'; Heading = 'English' },
    @{ File = 'CHANGELOG.md'; Code = 'es'; Heading = 'Español' }
)) {
    $source = Get-Content -LiteralPath (Join-Path $root $language.File) -Raw
    $versionPattern = [regex]::Escape($Version)
    $match = [regex]::Match($source, "(?ms)^## \[$versionPattern\] - [^\r\n]+\r?\n(?<body>.*?)(?=^## |^\[[^\]]+\]:|\z)")
    if (!$match.Success -or !$match.Groups['body'].Value.Trim()) {
        throw "No release notes for $Version in $($language.File)."
    }
    $body = $match.Groups['body'].Value.Trim()
    $body = $body -replace '!\[([^\]]+)\]\((docs/capturas/[^)]+)\)', ('<img src="' + $raw + '$2" width="900" alt="$1">')
    $body += "`n`n[$($language.File)]($repo/blob/v$Version/$($language.File))"
    Set-Content -LiteralPath (Join-Path $artifacts "release-notes.$($language.Code).md") -Value $body -Encoding utf8
    "## $($language.Heading)`n`n$body"
}
$combined = '[English](#english) · [Español](#español)' + "`n`n" + ($sections -join "`n`n")
Set-Content -LiteralPath (Join-Path $artifacts 'release-notes.md') -Value $combined -Encoding utf8
