<#
.SYNOPSIS
  Builds MakinoMCP into artifacts\layout (the folder you share), optionally mirrors it to a target and zips it.
.PARAMETER Target
  A folder (for example a network share) to mirror the layout into. An existing MakinoMCP.settings.json there is never touched,
  older versions\* folders are left alone, and current.txt is written last.
.PARAMETER Version
  Defaults to <Version> in src\MakinoMCP\MakinoMCP.csproj.
.PARAMETER Zip
  Also write artifacts\MakinoMCP-<version>-win-x64.zip and its .sha256.
#>
param([string]$Target, [string]$Version, [switch]$Zip)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$main = Join-Path $root 'src\MakinoMCP\MakinoMCP.csproj'
$launcher = Join-Path $root 'src\MakinoMCP.Launcher\MakinoMCP.Launcher.csproj'

function Run([string]$what, [scriptblock]$cmd) {
    & $cmd
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" }
}

if (-not $Version) {
    $Version = ([xml](Get-Content -LiteralPath $main -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $Version) { throw "No <Version> in $main; pass -Version." }
}
$Version = $Version.Trim()
if ($Version -notmatch '^[0-9A-Za-z][0-9A-Za-z.+-]*$') { throw "Bad version '$Version'." }

$artifacts = Join-Path $root 'artifacts'
$layout = Join-Path $artifacts 'layout'
$versionDir = Join-Path $layout "versions\$Version"

Run 'tests' { dotnet test (Join-Path $root 'MakinoMCP.slnx') -c Release --nologo }

if (Test-Path -LiteralPath $layout) { Remove-Item -LiteralPath $layout -Recurse -Force }
New-Item -ItemType Directory -Path $versionDir -Force | Out-Null

Run 'main publish' {
    dotnet publish $main -c Release -r win-x64 --self-contained -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:Version=$Version -o $versionDir --nologo
}
Run 'launcher publish' { dotnet publish $launcher -c Release -o $layout --nologo }

Get-ChildItem -LiteralPath $layout -File -Filter '*.pdb' | Remove-Item -Force
Get-ChildItem -LiteralPath $versionDir -File -Filter '*.pdb' | Remove-Item -Force
Copy-Item -LiteralPath (Join-Path $root 'MakinoMCP.settings.example.json') -Destination $layout
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $layout
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $layout
# current.txt last: a launcher that sees it finds the version folder complete.
[System.IO.File]::WriteAllText((Join-Path $layout 'current.txt'), $Version)
Write-Host "Layout ready: $layout (version $Version)"

if ($Target) {
    New-Item -ItemType Directory -Path (Join-Path $Target "versions\$Version") -Force | Out-Null
    # Copy this version into its own folder (replacing only that folder's files), never other versions.
    Copy-Item -Path (Join-Path $versionDir '*') -Destination (Join-Path $Target "versions\$Version") -Recurse -Force
    Get-ChildItem -LiteralPath $layout -File |
        Where-Object { $_.Name -notin 'current.txt', 'MakinoMCP.settings.json' } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $Target -Force }
    [System.IO.File]::WriteAllText((Join-Path $Target 'current.txt'), $Version)
    Write-Host "Mirrored to $Target (settings file untouched)."
}

if ($Zip) {
    $zipPath = Join-Path $artifacts "MakinoMCP-$Version-win-x64.zip"
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($layout, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText("$zipPath.sha256", "$hash  $(Split-Path $zipPath -Leaf)`n")
    Write-Host "Zip: $zipPath"
}
