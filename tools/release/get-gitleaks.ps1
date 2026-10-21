# Installs the pinned gitleaks used by the release scans into tools\bin (git-ignored, never published, no system
# install). The download is refused unless its SHA-256 equals the one gitleaks published for this version.
# Usage: .\tools\release\get-gitleaks.ps1   (does nothing when it is already there)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseLib.ps1')
$version = $script:GitleaksVersion
$sha256 = 'd29144deff3a68aa93ced33dddf84b7fdc26070add4aa0f4513094c8332afc4e'   # gitleaks_8.30.1_checksums.txt, windows_x64
$url = "https://github.com/gitleaks/gitleaks/releases/download/v$version/gitleaks_${version}_windows_x64.zip"
if (Test-Path $script:GitleaksExe) { Write-Host "gitleaks $version is already installed: $($script:GitleaksExe)"; return }

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('dcxmail-gitleaks-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
try {
    $zip = Join-Path $tmp 'gitleaks.zip'
    Invoke-WebRequest $url -OutFile $zip -UseBasicParsing
    $got = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($got -ne $sha256) { throw "gitleaks download has SHA-256 $got, expected $sha256 - refused" }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, (Join-Path $tmp 'x'))
    $dest = Split-Path $script:GitleaksExe -Parent
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item (Join-Path $tmp 'x\gitleaks.exe') $dest
    Copy-Item (Join-Path $tmp 'x\LICENSE') $dest -ErrorAction SilentlyContinue
    Write-Host "gitleaks $version installed to $dest (SHA-256 of the download verified)"
}
finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
