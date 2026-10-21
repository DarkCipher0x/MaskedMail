# Writes the sanitised public source of one release (ASCII only: Windows PowerShell 5.1 reads scripts as ANSI):
#   dist\<ver>\DarkCipherMaskedMail-<ver>-source.zip
# build.ps1 runs it; the release pipeline (release.ps1) publishes that zip and nothing else, so the public repository,
# the GitHub Release and the website all come from this one file.
# What goes in is decided by tools\public-allowlist.txt. A file that is neither allowed nor listed in
# tools\private-list.txt stops the build, so nothing new is published, or left out, without a decision. Every public
# file is then scanned (local paths, private e-mails, keys, Discord webhooks, the private terms in
# tools\privacy-terms.local.txt, and gitleaks with tools\gitleaks.toml); any hit stops the build with the file and line.
# Images are listed if nobody has approved them yet (tools\approved-images.txt); the release step refuses them.
# Usage: .\tools\write-public-source.ps1 -Version 0.3.3
param([Parameter(Mandatory)][string]$Version, [string]$OutDir)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release\ReleaseLib.ps1')
$root = $script:ProjectRoot
$dist = if ($OutDir) { $OutDir } else { Get-DistDir $Version }
if (-not (Test-Path $dist)) { throw "dist\$Version does not exist: build the release first" }

# 1. The file list, from the allowlist.
$split = Split-ByAllowlist @(Get-TreeFiles $root)
if ($split.Unclassified.Count) {
    $split.Unclassified | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    throw "$($split.Unclassified.Count) file(s) are in neither tools\public-allowlist.txt nor tools\private-list.txt (above): classify each one"
}
$files = @($split.Public)
if ($files.Count -lt 100) { throw "only $($files.Count) public files - is the allowlist right?" }

# 2. The scans.
$hits = @(Invoke-PrivacyScan $root $files)
if ($hits.Count) {
    $hits | Select-Object -First 40 | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    throw "privacy scan: $($hits.Count) line(s) must not be published (above). Fix the source; nothing was written."
}

# 3. The archive, entries written one by one with '/' separators (ZipFile.CreateFromDirectory on .NET Framework writes
# '\', which other platforms' unzip tools turn into odd file names). It is staged first so gitleaks scans exactly what
# goes in.
$name = Get-SourceName $Version
$stage = Join-Path ([IO.Path]::GetTempPath()) ('dcxmail-source-' + [guid]::NewGuid().ToString('N'))
try {
    foreach ($f in $files) {
        $dest = Join-Path $stage $f
        New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
        Copy-Item (Join-Path $root $f) $dest
    }
    $leaks = @(Invoke-Gitleaks $stage)
    if ($leaks.Count) {
        $leaks | Select-Object -First 40 | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        throw "gitleaks: $($leaks.Count) finding(s) (above). Fix the source; nothing was written."
    }
    $zip = Join-Path $dist "$name.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($f in $files) {
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $stage $f), "$name/$f", [IO.Compression.CompressionLevel]::Optimal)
        }
    } finally { $archive.Dispose() }
}
finally { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue }

$unseen = @(Get-UnapprovedImages $root $files)
Write-Host ("Source: {0} ({1} files, {2:N1} MB): allowlist, privacy scan and gitleaks clean ({3} private files left out)" -f $zip, $files.Count, ((Get-Item $zip).Length / 1MB), $split.Private.Count)
if ($unseen.Count) {
    Write-Host "Images not approved yet (view each, then add its SHA-256 to tools\approved-images.txt; release.ps1 refuses them until then):" -ForegroundColor Yellow
    $unseen | ForEach-Object { Write-Host ("  {0}  {1}" -f (Get-FileHash (Join-Path $root $_) -Algorithm SHA256).Hash.ToLowerInvariant(), $_) -ForegroundColor Yellow }
}
