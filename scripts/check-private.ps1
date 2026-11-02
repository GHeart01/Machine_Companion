<#
.SYNOPSIS
  Fails when a tracked file matches a private pattern (names, servers, customers that must not be published).
.PARAMETER Patterns
  Newline-separated regular expressions. Also read from the PRIVATE_PATTERNS environment variable.
.NOTES
  Output is file:line only. The patterns are never printed, because CI logs are public.
#>
param([string]$Patterns = $env:PRIVATE_PATTERNS)

$ErrorActionPreference = 'Stop'

try {
    $list = @()
    if ($Patterns) {
        $list = @($Patterns -split "\r?\n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    }
    if ($list.Count -eq 0) {
        Write-Host "::notice::No private patterns configured; nothing to check."
        exit 0
    }

    $regexes = @()
    foreach ($p in $list) {
        try { $regexes += [regex]::new($p, 'IgnoreCase') }
        catch { Write-Host "::error::A private pattern is not a valid regex"; exit 1 }
    }
    $root = (git rev-parse --show-toplevel).Trim()
    $hits = 0
    foreach ($rel in (git -C $root ls-files)) {
        $path = Join-Path $root $rel
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        $bytes = [System.IO.File]::ReadAllBytes($path)
        if ([Array]::IndexOf($bytes, [byte]0) -ge 0) { continue } # binary
        $n = 0
        foreach ($line in [System.IO.File]::ReadLines($path)) {
            $n++
            foreach ($re in $regexes) {
                if ($re.IsMatch($line)) { Write-Host "${rel}:${n} matched a private pattern"; $hits++; break }
            }
        }
    }
    if ($hits -gt 0) { Write-Host "$hits line(s) matched a private pattern."; exit 1 }
    Write-Host "No private patterns found."
    exit 0
} catch {
    Write-Host "::error::check-private failed unexpectedly (details withheld because logs are public)."
    exit 1
}
