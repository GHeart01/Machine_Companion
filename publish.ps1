<#
.SYNOPSIS
  Builds MakinoMCP into artifacts\layout and, with -Target, publishes it to a share. -Zip also writes the release zip.

.DESCRIPTION
  Every publish is a new build, <Version>-<yyyyMMdd-HHmm> (e.g. 0.2.0-20261106-0930), so it reaches every PC without
  raising <Version>: a PC copies each build once, by its folder name. Raise <Version> for a release (the GitHub tag and
  the zip name carry it).

  Share layout (-Target):
    MakinoMCP.exe, MakinoMCP.exe.config   the launcher: Claude starts it with "serve"; opened without arguments it is
                                          the setup window
    MakinoMCP.settings.json               the cell login and shared folders, written by the setup window (never
                                          shipped, never overwritten)
    MakinoMCP.settings.example.json       this build's example, replaced every publish
    current.txt                           the build the launcher starts
    versions\<build>\                     MakinoMCP.exe (self-contained, single file), build.txt, version.txt, LICENSE
    versions\old-launchers\               launchers replaced while Claude sessions still ran them; each publish deletes
                                          the ones no session holds open any more

  current.txt is switched to the new build last, so a Claude session starting during a publish still gets the previous,
  complete build. All but the newest three builds are then removed from the share, in the background. The launcher
  copies each build to %LOCALAPPDATA%\MakinoMCP\app\<build> and runs it there (the first session after a publish runs
  the share copy while the PC copies it), and removes the PC's older copies.

  Roll back: write an older build's folder name (see versions\) into current.txt.

.PARAMETER Target
  The share folder, e.g. \\server\Tools\MakinoMCP. Without it the app is only built into artifacts\layout.
.PARAMETER Version
  Defaults to <Version> in src\MakinoMCP\MakinoMCP.csproj.
.PARAMETER Zip
  Also write artifacts\MakinoMCP-<version>-win-x64.zip (the layout plus README.md and LICENSE) and its .sha256.
.PARAMETER ZipExisting
  Only write the zip, from the artifacts\layout an earlier run built, without testing or building again. The release
  workflow uses it after SignPath has signed the two exes in that layout.
.PARAMETER AllowOtherFiles
  Publish even though the target holds files this app did not put there.
.PARAMETER AllowBehindShare
  Publish even though this checkout does not contain the commit of the build the share runs now (its build.txt).
  Without it that publish stops, since it would remove whatever that build added; use it for a deliberate rollback.
#>
param([string]$Target, [string]$Version, [switch]$Zip, [switch]$ZipExisting, [switch]$AllowOtherFiles, [switch]$AllowBehindShare)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$main = Join-Path $root 'src\MakinoMCP\MakinoMCP.csproj'
$launcher = Join-Path $root 'src\MakinoMCP.Launcher\MakinoMCP.Launcher.csproj'
$launcherFiles = 'MakinoMCP.exe', 'MakinoMCP.exe.config'

function Run([string]$what, [scriptblock]$cmd) {
    & $cmd
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" }
}

function Get-Git([string[]]$arguments) {
    # Output is kept, and a copy without git history must still publish.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { return (& git -C $root @arguments 2>$null | Out-String).Trim() } catch { return '' } finally { $ErrorActionPreference = $previous }
}

# True when nobody has the file open: no Claude session runs this launcher any more (a running exe holds its file open
# on the file server, so an exclusive open fails).
function Test-Unused([string]$path) {
    try { ([IO.File]::Open($path, 'Open', 'Read', 'None')).Close(); return $true } catch { return $false }
}

# ── Version ────────────────────────────────────────────────────────────────────────────────────────────────────────

if (-not $Version) {
    $Version = ([xml](Get-Content -LiteralPath $main -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $Version) { throw "No <Version> in $main; pass -Version." }
}
$Version = $Version.Trim()
if ($Version -notmatch '^[0-9A-Za-z][0-9A-Za-z.+-]*$') { throw "Bad version '$Version'." }

$artifacts = Join-Path $root 'artifacts'
$layout = Join-Path $artifacts 'layout'

# The release zip is a GitHub install: its marker tells the launcher to update itself from this repository's releases
# (when unpacked on a PC's own disk). Shares never get it.
$markerName = 'MakinoMCP.update.json'
function Get-Repository {
    $url = Get-Git @('remote', 'get-url', 'origin')
    if ($url -match 'github\.com[/:]([A-Za-z0-9-]+)/([A-Za-z0-9._-]+?)(\.git)?$') { return "$($Matches[1])/$($Matches[2])" }
    return 'GHeart01/Machine_Companion'
}

# The release zip: the layout plus README.md, LICENSE and the update marker at its top, and its SHA-256 beside it.
function Write-Zip {
    $zipPath = Join-Path $artifacts "MakinoMCP-$Version-win-x64.zip"
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($layout, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
    $archive = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Update)
    try {
        foreach ($name in 'README.md', 'LICENSE') {
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $root $name), $name)
        }
        $writer = New-Object IO.StreamWriter ($archive.CreateEntry($markerName).Open())
        try { $writer.Write("{ `"source`": `"github`", `"repository`": `"$(Get-Repository)`" }`n") } finally { $writer.Dispose() }
    } finally { $archive.Dispose() }
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$zipPath.sha256", "$hash  $(Split-Path $zipPath -Leaf)`n")
    Write-Host "Zip: $zipPath"
}

if ($ZipExisting) {
    if ($Target -or $Zip) { throw '-ZipExisting only zips the existing layout; use it without -Target and -Zip.' }
    $existing = Join-Path $layout 'current.txt'
    if (-not (Test-Path -LiteralPath $existing)) { throw "No layout to zip: $layout has no current.txt. Run .\publish.ps1 first." }
    $existingBuild = "$(Get-Content -LiteralPath $existing -TotalCount 1)".Trim()
    if (-not $existingBuild.StartsWith("$Version-")) { throw "The layout holds build $existingBuild, not version $Version." }
    foreach ($required in "MakinoMCP.exe", "MakinoMCP.exe.config", "versions\$existingBuild\MakinoMCP.exe") {
        if (-not (Test-Path -LiteralPath (Join-Path $layout $required))) { throw "The layout has no $required" }
    }
    Write-Zip
    return
}
$build = "$Version-$(Get-Date -Format 'yyyyMMdd-HHmm')"
$commit = Get-Git @('rev-parse', '--short', 'HEAD')
if (-not $commit) { $commit = 'unknown' }
$dirty = [bool](Get-Git @('status', '--porcelain'))
Write-Host "MakinoMCP $build (commit $commit$(if ($dirty) { ', with uncommitted changes' }))"
if ($dirty) { Write-Warning 'The working tree has uncommitted changes; they are included in this build.' }

# ── Before any work: the target ───────────────────────────────────────────────────────────────────────────────────

if ($Target) {
    $Target = $Target.TrimEnd('\')
    if (Test-Path -LiteralPath $Target) {
        # README.md, LICENSE and *.old-* at the top are what earlier publishes left there; this one tidies them away.
        $ours = $launcherFiles + @('MakinoMCP.settings.json', 'MakinoMCP.settings.example.json', 'current.txt', 'current.txt.tmp',
            'versions', 'README.md', 'LICENSE', $markerName)
        $other = @(Get-ChildItem -LiteralPath $Target -Force | Where-Object { $ours -notcontains $_.Name -and $_.Name -notlike '*.old-*' -and $_.Name -notlike '*.old' })
        if ($other.Count -gt 0 -and -not $AllowOtherFiles) {
            $list = ($other | Select-Object -First 10 | ForEach-Object { "  $($_.Name)" }) -join "`n"
            throw "$Target holds files this app did not put there:`n$list`nPublish to an empty folder, or add -AllowOtherFiles if they belong there."
        }
    }

    $currentFile = Join-Path $Target 'current.txt'
    $live = if (Test-Path -LiteralPath $currentFile) { "$(Get-Content -LiteralPath $currentFile -TotalCount 1)".Trim() } else { '' }
    if ($live -eq $build) { throw "The share already runs $build (published this minute). Wait a minute and publish again." }

    # The build people run now must be part of this one, or publishing would remove what it added.
    $liveInfo = if ($live) { Join-Path $Target "versions\$live\build.txt" } else { '' }
    if ($commit -ne 'unknown' -and $liveInfo -and (Test-Path -LiteralPath $liveInfo)) {
        $info = @{}
        foreach ($line in Get-Content -LiteralPath $liveInfo) { $k, $v = $line -split '=', 2; if ($null -ne $v) { $info[$k] = $v } }
        $liveCommit = "$($info['commit'])".Trim()
        if ($liveCommit -and $liveCommit -ne 'unknown') {
            $previous = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try { & git -C $root merge-base --is-ancestor $liveCommit HEAD 2>$null; $code = $LASTEXITCODE } finally { $ErrorActionPreference = $previous }
            if ($code -eq 1) {
                $why = "The share runs $live from commit $liveCommit, which this checkout (commit $commit) does not contain: publishing would remove what it added."
                if (-not $AllowBehindShare) { throw "$why Merge it in, or add -AllowBehindShare to publish anyway (a rollback)." }
                Write-Warning $why
            } elseif ($code -ne 0) {
                Write-Warning "Could not check that this checkout contains the share's build $live (commit $liveCommit is not in this repository)."
            }
        }
    }
}

# ── Tests and build ───────────────────────────────────────────────────────────────────────────────────────────────

Run 'tests' { dotnet test (Join-Path $root 'MakinoMCP.slnx') -c Release --nologo }

$buildDir = Join-Path $layout "versions\$build"
if (Test-Path -LiteralPath $layout) { Remove-Item -LiteralPath $layout -Recurse -Force }
New-Item -ItemType Directory -Path $buildDir -Force | Out-Null

Run 'main publish' {
    dotnet publish $main -c Release -r win-x64 --self-contained -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:Version=$Version `
        -p:IncludeSourceRevisionInInformationalVersion=false -o $buildDir --nologo
}
# Both exes carry the plain version as their product version (no build stamp, no commit): SignPath wants one product
# version across a build, and publishes of one version then leave the launcher's hash, and the launcher on the share,
# alone. The build stamp is in the folder name, version.txt and build.txt.
Run 'launcher publish' {
    dotnet publish $launcher -c Release -p:Version=$Version -p:IncludeSourceRevisionInInformationalVersion=false `
        -o $layout --nologo
}

Get-ChildItem -LiteralPath $layout -File -Filter '*.pdb' | Remove-Item -Force
Get-ChildItem -LiteralPath $buildDir -File -Filter '*.pdb' | Remove-Item -Force
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $buildDir
Copy-Item -LiteralPath (Join-Path $root 'MakinoMCP.settings.example.json') -Destination $layout
[IO.File]::WriteAllText((Join-Path $buildDir 'version.txt'), $build)
[IO.File]::WriteAllLines((Join-Path $buildDir 'build.txt'), [string[]]@(
    "build=$build", "commit=$commit", "uncommittedChanges=$dirty", "publishedBy=$env:USERDOMAIN\$env:USERNAME",
    "machine=$env:COMPUTERNAME", "at=$(Get-Date -Format s)"))
# current.txt last: a launcher that sees it finds the build folder complete.
[IO.File]::WriteAllText((Join-Path $layout 'current.txt'), $build)
foreach ($required in "MakinoMCP.exe", "MakinoMCP.exe.config", "versions\$build\MakinoMCP.exe") {
    if (-not (Test-Path -LiteralPath (Join-Path $layout $required))) { throw "The layout has no $required" }
}
Write-Host "Layout ready: $layout (build $build)"

# ── Share ─────────────────────────────────────────────────────────────────────────────────────────────────────────

if ($Target) {
    $versions = Join-Path $Target 'versions'
    $oldLaunchers = Join-Path $versions 'old-launchers'
    $targetBuild = Join-Path $versions $build

    # 1. This build, complete, in its own folder. A folder left by a failed publish in this same minute starts afresh.
    if (Test-Path -LiteralPath $targetBuild) { Remove-Item -LiteralPath $targetBuild -Recurse -Force }
    New-Item -ItemType Directory -Path $targetBuild -Force | Out-Null
    Copy-Item -Path (Join-Path $buildDir '*') -Destination $targetBuild -Recurse -Force
    foreach ($f in Get-ChildItem -LiteralPath $buildDir -File) {
        $copy = Get-Item -LiteralPath (Join-Path $targetBuild $f.Name) -ErrorAction SilentlyContinue
        if (-not $copy -or $copy.Length -ne $f.Length) {
            throw "The copy on the share is incomplete ($($f.Name)). current.txt still names $live."
        }
    }

    # 2. The launcher runs for a whole Claude session, so it can't be overwritten while anyone is connected. Replace
    #    it only when it changed; one still in use is moved into versions\old-launchers (Windows allows moving a
    #    running exe), one nobody uses is deleted.
    New-Item -ItemType Directory -Path $oldLaunchers -Force | Out-Null
    $stamp = Get-Date -Format 'yyyyMMddHHmmss'
    foreach ($name in $launcherFiles) {
        $new = Join-Path $layout $name
        $old = Join-Path $Target $name
        if (Test-Path -LiteralPath $old) {
            if ((Get-FileHash -LiteralPath $new -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $old -Algorithm SHA256).Hash) {
                Write-Host "$name unchanged, left in place."
                continue
            }
            $aside = Join-Path $oldLaunchers "$name.$stamp"
            try { Move-Item -LiteralPath $old -Destination $aside }
            catch {
                throw ("$old could not be moved aside ($($_.Exception.Message)). versions\$build was copied but NOT " +
                    "activated: current.txt still names $live. Try again in a minute.")
            }
            try { Copy-Item -LiteralPath $new -Destination $old }
            catch {
                # Never leave the share without a launcher: put the old one back.
                Move-Item -LiteralPath $aside -Destination $old
                throw ("Could not copy the new $name to $Target ($($_.Exception.Message)). versions\$build was copied " +
                    "but NOT activated: current.txt still names $live.")
            }
            Write-Host "$name replaced (the old one is in versions\old-launchers until no session uses it)."
        } else {
            Copy-Item -LiteralPath $new -Destination $old
        }
    }

    # 3. The example settings beside the launcher; the share's own MakinoMCP.settings.json is never touched.
    Copy-Item -LiteralPath (Join-Path $layout 'MakinoMCP.settings.example.json') -Destination $Target -Force

    # 4. current.txt last, swapped in whole: a launcher that reads it finds the build folder complete.
    $currentTmp = Join-Path $Target 'current.txt.tmp'
    [IO.File]::WriteAllText($currentTmp, $build)
    Move-Item -LiteralPath $currentTmp -Destination (Join-Path $Target 'current.txt') -Force
    Write-Host "Published $build to $Target (settings file untouched)."

    # 5. Tidy up. Launchers set aside earlier (in old-launchers, or *.old-* at the top from publishes before this
    #    layout): deleted when no session holds them open, otherwise moved into old-launchers for a later publish.
    #    README.md and LICENSE at the top came from those publishes too (LICENSE now ships in each build).
    $setAside = @(Get-ChildItem -LiteralPath $oldLaunchers -File) +
        @(Get-ChildItem -LiteralPath $Target -File | Where-Object { $_.Name -like 'MakinoMCP.exe*.old-*' -or $_.Name -like 'MakinoMCP.exe*.old' })
    foreach ($f in $setAside) {
        if (Test-Unused $f.FullName) { Remove-Item -LiteralPath $f.FullName -Force; Write-Host "Deleted old launcher $($f.Name)" }
        elseif ($f.DirectoryName -ne $oldLaunchers) {
            try { Move-Item -LiteralPath $f.FullName -Destination (Join-Path $oldLaunchers "$($f.Name).$stamp") } catch { Write-Warning "Could not move $($f.Name): $($_.Exception.Message)" }
        }
    }
    foreach ($name in 'README.md', 'LICENSE') { Remove-Item -LiteralPath (Join-Path $Target $name) -Force -ErrorAction SilentlyContinue }
    # A share filled from a release zip carries the GitHub marker; a share is updated by publishing, never from GitHub.
    $marker = Join-Path $Target $markerName
    if (Test-Path -LiteralPath $marker) { Remove-Item -LiteralPath $marker -Force; Write-Host "Removed ${markerName}: a share updates by publishing, not from GitHub." }

    # Keep the newest three builds on the share, and always the one that was live until now (a rollback target). A
    # PC's first session after a publish runs the build straight from the share, so a build whose exe is still open is
    # kept for a later publish. Deleting over the network can take a while, so it goes on after the publish ends, in a
    # hidden window; a build left half-deleted is deleted again by the next publish.
    Get-ChildItem -LiteralPath $versions -Directory |
        Where-Object { $_.Name -ne $build -and $_.Name -ne $live -and $_.Name -ne 'old-launchers' } |
        Sort-Object CreationTimeUtc -Descending |
        Select-Object -Skip 1 |
        Where-Object {
            $exe = Join-Path $_.FullName 'MakinoMCP.exe'
            if ((Test-Path -LiteralPath $exe) -and -not (Test-Unused $exe)) { Write-Host "Keeping old build $($_.Name): a session still runs it"; $false } else { $true }
        } |
        ForEach-Object {
            Start-Process -FilePath 'cmd.exe' -ArgumentList "/d /c rmdir /s /q `"$($_.FullName)`"" -WindowStyle Hidden `
                -WorkingDirectory ([IO.Path]::GetTempPath())
            Write-Host "Removing old build $($_.Name) in the background"
        }
}

# ── Release zip ───────────────────────────────────────────────────────────────────────────────────────────────────

if ($Zip) { Write-Zip }
