# Release step "release": the GitHub Release "MaskedMail <ver>" on tag v<ver> (pushed by the mirror step).
#   Notes   docs/release-notes/<ver>.md from the staged public copy (the same text as the repository), plus the downloads
#   Files   the MSI, the portable zip, the source zip, SHA256SUMS.txt, the test and self-test reports, all from
#           dist\<ver>\ (checked by the verify step), and the latest sample add-ons with their .sha256 files
# GitHub reports its own SHA-256 of every upload; it must equal ours. A release that exists is completed, never changed:
# missing files are uploaded, present files must match, and a mismatch stops the step (fix: a new version).
# Sign-in: the GitHub token Git Credential Manager already holds, in memory only.
param([Parameter(Mandatory)][string]$Version, [switch]$DryRun)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseLib.ps1')
$cfg = Get-ReleaseConfig
$api = $cfg.publicRepoApi
$dist = Get-DistDir $Version
$tag = "v$Version"
$top = Join-Path (Get-StagingDir $Version) (Get-SourceName $Version)
if (-not (Test-Path $top)) { throw "no public copy staged for ${Version}: run the sanitise step first" }

# 1. The files, each checked before it goes anywhere.
$assets = @("DarkCipherMaskedMail-$Version-x64.msi", "DarkCipher-MaskedMail-$Version-win-x64.zip", "DarkCipherMaskedMail-$Version-source.zip",
    'SHA256SUMS.txt', "tests-$Version.txt", "selftest-$Version.txt") | ForEach-Object { Join-Path $dist $_ }
foreach ($id in Get-ChildItem (Join-Path $script:ProjectRoot 'dist\addons') -Directory -ErrorAction SilentlyContinue) {
    $pkg = Get-ChildItem $id.FullName -Filter '*.dcxaddon' | Sort-Object { [version]([regex]::Match($_.BaseName, '(\d+\.\d+\.\d+)$').Groups[1].Value) } | Select-Object -Last 1
    if (-not $pkg) { continue }
    $sidecar = "$($pkg.FullName).sha256"
    if (-not (Test-Path $sidecar)) { throw "$($pkg.Name) has no .sha256 file" }
    if ([IO.File]::ReadAllText($sidecar) -notmatch (Get-FileHash $pkg.FullName -Algorithm SHA256).Hash) { throw "$($pkg.Name) does not match its .sha256 file" }
    $assets += $pkg.FullName, $sidecar
}
foreach ($a in $assets) { if (-not (Test-Path $a)) { throw "missing $a" } }

# 2. The notes: the release notes from the public copy, then what to download.
$notes = [IO.File]::ReadAllText((Join-Path $top "docs\release-notes\$Version.md"))
$notes = ($notes -replace '^# [^\n]*\n', '').Trim()
$body = @"
## What's new in $Version

$notes

## Download

| File | What it is |
|---|---|
| ``DarkCipherMaskedMail-$Version-x64.msi`` | The installer (Windows 10/11 x64). Upgrades any earlier version in place. |
| ``DarkCipher-MaskedMail-$Version-win-x64.zip`` | Portable: the same exe plus the docs. |
| ``DarkCipherMaskedMail-$Version-source.zip`` | The source this release was built from (the same files as this repository at ``$tag``). |
| ``SHA256SUMS.txt`` | SHA-256 of every file in the release folder. |
| ``tests-$Version.txt``, ``selftest-$Version.txt`` | The unit-test and self-test reports from this build. |
| ``*.dcxaddon`` | The sample add-ons, each with its ``.sha256``. |

The MSI isn't code-signed yet, so Windows SmartScreen will warn. Check it against ``SHA256SUMS.txt`` first:
``Get-FileHash .\DarkCipherMaskedMail-$Version-x64.msi -Algorithm SHA256``
"@

# 3. GitHub: the tag must be there (the mirror step pushes it), then create or complete the release.
$h = Get-GitHubHeaders
$tagRef = Get-GitHubOrNull "$api/git/ref/tags/$tag" $h
if (-not $tagRef) {
    if ($DryRun) { return New-StepResult 'would-do' "would create release $tag (after the mirror step pushes the tag) with $($assets.Count) files" }
    throw "tag $tag is not on GitHub: run the mirror step first"
}
$rel = Get-GitHubOrNull "$api/releases/tags/$tag" $h
if (-not $rel) {
    if ($DryRun) { return New-StepResult 'would-do' "would create release $tag with $($assets.Count) files" }
    $json = @{ tag_name = $tag; name = "MaskedMail $Version"; body = $body; draft = $false; prerelease = $false; make_latest = 'true' } | ConvertTo-Json
    $rel = Invoke-RestMethod "$api/releases" -Method Post -Headers $h -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($json))
    Write-Host "  created release $tag"
}
$present = @{}
foreach ($x in @($rel.assets)) { if ($x) { $present[$x.name] = $x } }
$uploaded = 0; $matched = 0; $todo = @()
foreach ($a in $assets) {
    $n = Split-Path $a -Leaf
    $local = 'sha256:' + (Get-FileHash $a -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($present.ContainsKey($n)) {
        if ($present[$n].digest -and $present[$n].digest -ne $local) { throw "GitHub already has $n with a different SHA-256 ($($present[$n].digest)). A published file is never replaced: release a new version." }
        $matched++; continue
    }
    if ($DryRun) { $todo += $n; continue }
    $up = ($rel.upload_url -replace '\{.*\}$', '') + '?name=' + [uri]::EscapeDataString($n)
    $ct = if ($n -like '*.txt' -or $n -like '*.sha256' -or $n -like '*.md') { 'text/plain' } elseif ($n -like '*.zip') { 'application/zip' } else { 'application/octet-stream' }
    $r = Invoke-RestMethod $up -Method Post -Headers $h -ContentType $ct -InFile $a -TimeoutSec 1800
    if ($r.digest -and $r.digest -ne $local) { throw "GitHub stored $n with a different SHA-256 ($($r.digest))" }
    Write-Host ("  uploaded {0} ({1:N1} MB), SHA-256 confirmed" -f $n, ($r.size / 1MB))
    $uploaded++
}
if ($DryRun -and $todo.Count) { return New-StepResult 'would-do' "release $tag exists; would upload $($todo.Count) missing file(s): $($todo -join ', ')" }
if ($uploaded -eq 0) { return New-StepResult 'skipped' "release $tag already has all $matched files, every SHA-256 matching" }
New-StepResult 'done' "release $tag ($($rel.html_url)): $uploaded uploaded, $matched already there, every SHA-256 matching"
