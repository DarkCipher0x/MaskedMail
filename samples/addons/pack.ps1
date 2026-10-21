# Packs an add-on folder into a .dcxaddon file (a zip with addon.json at its root).
# Output: dist\addons\<add-on id>\<id>-<version>.dcxaddon plus <file>.sha256 ("<sha256>  <file>", sha256sum format).
# Each add-on has its own folder; every version stays in it (an existing version is never overwritten: bump "version").
# Usage: .\pack.ps1 .\hello-hosts
param([Parameter(Mandatory = $true)][string]$Folder)
$ErrorActionPreference = 'Stop'
$src = (Resolve-Path $Folder).Path
$manifest = [IO.File]::ReadAllText((Join-Path $src 'addon.json')) | ConvertFrom-Json
if ($manifest.id -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$' -or "$($manifest.version)" -notmatch '^[0-9A-Za-z.+-]+$') { throw 'addon.json needs a plain "id" and "version"' }
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$dir = Join-Path $repo "dist\addons\$($manifest.id)"
New-Item -ItemType Directory -Force $dir | Out-Null
$name = "{0}-{1}.dcxaddon" -f $manifest.id, $manifest.version
$out = Join-Path $dir $name
if (Test-Path $out) { throw "$out already exists - bump ""version"" in addon.json (earlier versions are kept)" }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($src, $out)
$sha = (Get-FileHash $out -Algorithm SHA256).Hash.ToLower()
[IO.File]::WriteAllText("$out.sha256", "$sha  $name`n", (New-Object Text.UTF8Encoding $false))
Write-Host "Packed $out"
Write-Host "SHA-256 $sha"
# Every packaged add-on also gets a zipped copy: dist\addons\<id>.zip holds the <id> folder (all versions + .sha256).
$zip = Join-Path $repo "dist\addons\$($manifest.id).zip"
if (Test-Path $zip) { Remove-Item $zip }
[IO.Compression.ZipFile]::CreateFromDirectory($dir, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
Write-Host "Zipped $zip"
