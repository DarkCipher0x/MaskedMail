# Writes the sanitised public source of one release (ASCII only: Windows PowerShell 5.1 reads scripts as ANSI).
#   dist\<ver>\DarkCipherMaskedMail-<ver>-source.zip
# and, with -Mirror <folder>, the same files into a git working tree (the GitHub repository), replacing everything there
# except .git. What goes in: the source tree minus build output, releases, local tool caches and *.local.txt files.
# Before anything is written, every text file is scanned for things that must never be published: local user paths,
# private e-mail addresses, keys, and the terms in tools\privacy-terms.local.txt (one per line; never published).
# A hit stops with the file and line. Usage: .\tools\write-public-source.ps1 -Version 0.3.1 [-Mirror ..\MaskedMail-public]
param([Parameter(Mandatory)][string]$Version, [string]$Mirror)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root "dist\$Version"
if (-not (Test-Path $dist)) { throw "dist\$Version does not exist: build the release first" }

# 1. The file list. Folders and files that are never source:
$skipDir = '^(dist|publish|\.vs|\.git|\.selftest[^/]*|TestResults)/|/(bin|obj)/|^(bin|obj)/'
$skipFile = '\.local\.txt$|_wpftmp\.csproj$|\.user$|\.(pfx|p12|pem|key|cer|kdbx|ppk|pdb|wixpdb|msix)$'
$files = Get-ChildItem $root -Recurse -File -Force | ForEach-Object { $_.FullName.Substring($root.Length + 1).Replace('\', '/') } |
    Where-Object { $_ -notmatch $skipDir -and $_ -notmatch $skipFile } | Sort-Object
if ($files.Count -lt 100) { throw "only $($files.Count) files listed - is this the project root?" }

# 2. The privacy scan. Keys and user paths in the tests and fakes are synthetic fixtures, so those rules skip them; the
# term list applies everywhere. Allowed e-mail domains: documentation and lab TLDs, the services' own public domains
# that appear in examples, DarkCipher's, GitHub noreply and Anthropic (commit trailers).
$rules = @(
    @{ Name = 'a local user path'; Rx = '(?i)[A-Z]:\\Users\\(?!Public\\|demo\\|<|you\\|%)[^\\\s"'']+'; SkipTests = $true },
    @{ Name = 'a private e-mail address'; Rx = '(?i)\b[A-Z0-9._%+-]+@(?!example\.|[a-z0-9.-]*\.(test|example|invalid|local|lan)\b|darkciphersystems\.com|anthropic\.com|users\.noreply\.github\.com|[a-z0-9.-]*addy\.io|simplelogin\.com|duck\.com|mozmail\.com)[A-Z0-9.-]+\.[A-Z]{2,}\b'; SkipTests = $true },
    @{ Name = 'an API key or token'; Rx = '\b(sk-[A-Za-z0-9]{20,}|ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|fmu1-[A-Za-z0-9-]{20,}|AKIA[0-9A-Z]{16})\b'; SkipTests = $true },
    @{ Name = 'a private key'; Rx = '-----BEGIN [A-Z ]*PRIVATE KEY-----'; SkipTests = $false }
)
$termsFile = Join-Path $PSScriptRoot 'privacy-terms.local.txt'
$terms = @()
if (Test-Path $termsFile) { $terms = @([IO.File]::ReadAllLines($termsFile) | Where-Object { $_.Trim().Length -gt 0 -and -not $_.StartsWith('#') } | ForEach-Object { $_.Trim() }) }
else { throw 'tools\privacy-terms.local.txt not found: the private names must be checked before anything is published' }
# A one-word term matches as a whole word; a term with punctuation (a domain, a path) matches anywhere.
$termRx = $terms | ForEach-Object { if ($_ -match '^\w+$') { '(?i)\b' + [regex]::Escape($_) + '\b' } else { '(?i)' + [regex]::Escape($_) } }
$termRx += '(?i)\b' + [regex]::Escape($env:USERNAME) + '\b'
$binExt = '.png', '.ico', '.dll', '.rtf', '.zip', '.exe'
$hits = New-Object System.Collections.Generic.List[string]
foreach ($f in $files) {
    $path = Join-Path $root $f
    $ext = [IO.Path]::GetExtension($f).ToLowerInvariant()
    $isTest = $f -match '^tests/|^src/DcxMail\.Fakes/|/SelfTest\.cs$'
    if ($ext -in $binExt) {
        # Binaries: only the user name and path rules (a DLL can carry its build path).
        $raw = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($path))
        if ($raw -match '(?i)[A-Z]:\\Users\\(?!Public\\)[^\\\x00]+' -or $raw -match ('(?i)' + [regex]::Escape($env:USERNAME))) { $hits.Add("${f}: a local path or user name inside a binary") }
        continue
    }
    $lines = [IO.File]::ReadAllLines($path)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $l = $lines[$i]
        foreach ($r in $rules) { if (-not ($isTest -and $r.SkipTests) -and $l -match $r.Rx) { $hits.Add("$f($($i + 1)): $($r.Name): $($Matches[0])") } }
        foreach ($t in $termRx) { if ($l -match $t) { $hits.Add("$f($($i + 1)): a private term") } }
    }
}
if ($hits.Count -gt 0) {
    $hits | Select-Object -First 40 | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    throw "privacy scan: $($hits.Count) line(s) must not be published (above). Fix the source; nothing was written."
}

# 3. The archive, with the folder name as its top level.
$name = "DarkCipherMaskedMail-$Version-source"
$zip = Join-Path $dist "$name.zip"
if (Test-Path $zip) { Remove-Item $zip }
$stage = Join-Path ([IO.Path]::GetTempPath()) ('dcxmail-source-' + [guid]::NewGuid().ToString('N'))
$top = Join-Path $stage $name
foreach ($f in $files) {
    $dest = Join-Path $top $f
    New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
    Copy-Item (Join-Path $root $f) $dest
}
# Entries are written one by one with '/' separators (ZipFile.CreateFromDirectory on .NET Framework writes '\', which
# other platforms' unzip tools turn into odd file names).
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($f in $files) {
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $root $f), "$name/$f", [IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $archive.Dispose() }
Write-Host ("Source: {0} ({1} files, {2:N1} MB), privacy scan clean ({3} rules, {4} private terms)" -f $zip, $files.Count, ((Get-Item $zip).Length / 1MB), $rules.Count, $terms.Count)

# 4. The repository mirror: the working tree becomes exactly this file list (git shows what changed).
if ($Mirror) {
    $Mirror = [IO.Path]::GetFullPath($Mirror)
    if (-not (Test-Path (Join-Path $Mirror '.git'))) { throw "$Mirror is not a git working tree" }
    Get-ChildItem $Mirror -Force | Where-Object Name -ne '.git' | Remove-Item -Recurse -Force
    Copy-Item (Join-Path $top '*') $Mirror -Recurse -Force
    Write-Host "Mirrored into $Mirror"
}
Remove-Item $stage -Recurse -Force
