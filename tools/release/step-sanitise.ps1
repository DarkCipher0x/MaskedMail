# Release step "sanitise": rebuilds the public copy of this version from scratch, from the release's own source zip
# (dist\<ver>\DarkCipherMaskedMail-<ver>-source.zip, already checked against SHA256SUMS by the verify step), into
# %TEMP%\dcxmail-public-<ver>\. The mirror, the GitHub Release notes and the website all read from there and nothing
# else. Before anything leaves this PC, every file in it must pass again:
#   - the allowlist (tools\public-allowlist.txt) and the private list: nothing private, nothing unclassified;
#   - no key, certificate, symbol or package files;
#   - every image approved by a person (tools\approved-images.txt);
#   - the privacy scan and gitleaks.
# It only writes to its staging folder, so a dry run does the full job.
param([Parameter(Mandatory)][string]$Version, [switch]$DryRun)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseLib.ps1')
$name = Get-SourceName $Version
$zip = Join-Path (Get-DistDir $Version) "$name.zip"
if (-not (Test-Path $zip)) { throw "no source zip for $Version ($zip)" }

# 1. Delete and recreate the staging folder: never patched, always rebuilt.
$staging = Get-StagingDir $Version
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory($zip, $staging)
$tops = @(Get-ChildItem $staging -Force)
if ($tops.Count -ne 1 -or $tops[0].Name -ne $name) { throw "the source zip must hold exactly one folder, $name/" }
$top = $tops[0].FullName
$files = @(Get-ChildItem $top -Recurse -File -Force | ForEach-Object { $_.FullName.Substring($top.Length + 1).Replace('\', '/') } | Sort-Object)

# 2. The gates. Each one lists everything it found before failing, so one run shows the whole problem.
$problems = New-Object System.Collections.Generic.List[string]
$split = Split-ByAllowlist $files
$split.Private | ForEach-Object { $problems.Add("$_ : on the private list") }
$split.Unclassified | ForEach-Object { $problems.Add("$_ : not in tools\public-allowlist.txt") }
$files | Where-Object { $_ -match '\.(pdb|pfx|p12|snk|key|pem|cer|pvk|msix|appx|kdbx|ppk)$' } | ForEach-Object { $problems.Add("$_ : key, certificate, symbol or package file") }
Get-UnapprovedImages $top $files | ForEach-Object { $problems.Add("$_ : image not approved (sha256 $((Get-FileHash (Join-Path $top $_) -Algorithm SHA256).Hash.ToLowerInvariant()))") }
Invoke-PrivacyScan $top $files | ForEach-Object { $problems.Add($_) }
Invoke-Gitleaks $top | ForEach-Object { $problems.Add($_) }
if ($problems.Count) {
    $problems | Select-Object -First 60 | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    throw "the public copy of $Version failed $($problems.Count) check(s) (above); nothing was published"
}

# 3. A fingerprint of exactly what is public, for the record (hash of every file's hash and path).
$lines = $files | ForEach-Object { '{0}  {1}' -f (Get-FileHash (Join-Path $top $_) -Algorithm SHA256).Hash.ToLowerInvariant(), $_ }
$sha = [Security.Cryptography.SHA256]::Create()
$print = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($lines -join "`n")))) -replace '-', '').ToLowerInvariant()
New-StepResult 'done' "$($files.Count) files in %TEMP%\dcxmail-public-$Version passed the allowlist, image, privacy and gitleaks checks (fingerprint $($print.Substring(0, 16)))"
