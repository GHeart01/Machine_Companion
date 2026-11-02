<#
.SYNOPSIS
  Builds MakinoMCP into artifacts\layout (the folder you share), optionally mirrors it to a target and zips it.
.PARAMETER Target
  A folder (for example a network share) to mirror the layout into. An existing MakinoMCP.settings.json there is never touched
  and older versions\* folders are left alone. A version already in versions\ there is refused (PCs that have used it keep
  their local copy and would never see the change): raise <Version> instead. The launcher (MakinoMCP.exe and its .config)
  is replaced only when it changed, after renaming the running one aside. current.txt is written last.
.PARAMETER Version
  Defaults to <Version> in src\MakinoMCP\MakinoMCP.csproj.
.PARAMETER Overwrite
  With -Target: replace versions\<version> even though it is already there. Only for a version no PC has used yet.
.PARAMETER Zip
  Also write artifacts\MakinoMCP-<version>-win-x64.zip and its .sha256.
#>
param([string]$Target, [string]$Version, [switch]$Overwrite, [switch]$Zip)

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

# Checked before building, and before anything at -Target changes. A PC that has already copied a version never copies it
# again, so re-publishing the same version would not reach it.
if ($Target -and -not $Overwrite -and (Test-Path -LiteralPath (Join-Path $Target "versions\$Version"))) {
    throw ("Version $Version is already at $(Join-Path $Target "versions\$Version"). Nothing was copied. PCs that have " +
        "used it keep their own copy and would not see a change: raise <Version> in src\MakinoMCP\MakinoMCP.csproj and " +
        "publish again. Use -Overwrite only for a version no PC has used yet.")
}

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
    $targetVersion = Join-Path $Target "versions\$Version"
    New-Item -ItemType Directory -Path $targetVersion -Force | Out-Null
    # 1. This version, complete, in its own folder; other versions are never touched.
    Copy-Item -Path (Join-Path $versionDir '*') -Destination $targetVersion -Recurse -Force

    # 2. The launcher runs for a whole Claude session on every PC, so it can't be overwritten while anyone is connected.
    #    Copy it only when it changed, after renaming the old one aside (Windows allows renaming a running exe).
    $launcherFiles = 'MakinoMCP.exe', 'MakinoMCP.exe.config'
    $stamp = Get-Date -Format 'yyyyMMddHHmmss'
    foreach ($name in $launcherFiles) {
        $new = Join-Path $layout $name
        $old = Join-Path $Target $name
        if (-not (Test-Path -LiteralPath $new)) { continue }
        $aside = $null
        if (Test-Path -LiteralPath $old) {
            if ((Get-FileHash -LiteralPath $new -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $old -Algorithm SHA256).Hash) {
                Write-Host "$name unchanged, left in place."
                continue
            }
            try { Rename-Item -LiteralPath $old -NewName "$name.old-$stamp" }
            catch {
                throw ("$old is in use and could not be renamed aside ($($_.Exception.Message)). versions\$Version was " +
                    "copied but NOT activated: current.txt still names the previous version. Try again when it is free; " +
                    "no PC has used $Version yet, so add -Overwrite.")
            }
            $aside = Join-Path $Target "$name.old-$stamp"
            Write-Host "Old $name renamed to $name.old-$stamp (delete it once no one is using it)."
        }
        try { Copy-Item -LiteralPath $new -Destination $old }
        catch {
            # Never leave the share without a launcher: put the old one back.
            if ($aside) { Rename-Item -LiteralPath $aside -NewName $name }
            throw ("Could not copy the new $name to $Target ($($_.Exception.Message)). versions\$Version was copied but " +
                "NOT activated: current.txt still names the previous version.")
        }
    }

    # 3. Everything else beside the launcher, never the settings file.
    Get-ChildItem -LiteralPath $layout -File |
        Where-Object { $_.Name -notin (@('current.txt', 'MakinoMCP.settings.json') + $launcherFiles) } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $Target -Force }

    # 4. current.txt last, swapped in whole: a launcher that reads it finds the version folder complete.
    $currentTmp = Join-Path $Target 'current.txt.tmp'
    [System.IO.File]::WriteAllText($currentTmp, $Version)
    Move-Item -LiteralPath $currentTmp -Destination (Join-Path $Target 'current.txt') -Force
    Write-Host "Published $Version to $Target (settings file untouched)."
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
