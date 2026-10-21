# Writes dist\<ver>\release-manifest.json: what the website publishes for this version, in the shape every DarkCipher app
# hands to the website (Website & Deployments' sync-downloads.ps1 reads it, and verifies the live site against it).
# Each file is listed with its role, the path it gets on the site, its size and SHA-256, and the GitHub Release asset it
# must equal byte for byte. The step-github-release.ps1 step runs this once the release is complete.
# Usage: .\tools\release\write-manifest.ps1 -Version 0.3.3
param([Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseLib.ps1')
$cfg = Get-ReleaseConfig
$dist = Get-DistDir $Version
$tag = "v$Version"
$repo = ($cfg.publicRemote -replace '^https://github\.com/', '') -replace '\.git$', ''
$clone = [IO.Path]::GetFullPath((Join-Path $script:ProjectRoot $cfg.publicClone))
$commit = (& git -C $clone rev-list -n 1 $tag)
if ($LASTEXITCODE -or -not $commit) { throw "tag $tag is not in the public clone: run the mirror step first" }
$base = "https://github.com/$repo/releases/download/$tag"
$files = foreach ($f in @(
        @{ File = "DarkCipherMaskedMail-$Version-x64.msi"; Role = 'installer'; Label = 'Windows installer (.msi)'; Site = 'downloads' },
        @{ File = "DarkCipher-MaskedMail-$Version-win-x64.zip"; Role = 'portable'; Label = 'Portable (.zip)'; Site = 'downloads' },
        @{ File = "DarkCipherMaskedMail-$Version-source.zip"; Role = 'source'; Label = 'Source code (.zip)'; Site = 'downloads/source' })) {
    $item = Get-Item (Join-Path $dist $f.File)
    [ordered]@{
        file = $f.File; role = $f.Role; label = $f.Label; site_path = "$($f.Site)/$($f.File)"
        bytes = $item.Length; sha256 = (Get-FileHash $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        github_asset = "$base/$($f.File)"
    }
}
$notes = [IO.File]::ReadAllText((Join-Path $dist "RELEASE-NOTES-$Version.md"))
$manifest = [ordered]@{
    app = 'maskedmail'; version = $Version
    released = (Get-Item (Join-Path $dist "DarkCipherMaskedMail-$Version-x64.msi")).LastWriteTime.ToString('yyyy-MM-dd')
    github_release = "https://github.com/$repo/releases/tag/$tag"
    mirror = [ordered]@{ repo = $repo; tag = $tag; commit = "$commit".Trim() }
    rule = 'Publish exactly these files under these paths; each equals its GitHub Release asset byte for byte. The source zip is the public repository at the tag (top folder DarkCipherMaskedMail-<ver>-source/).'
    files = @($files)
    notes_markdown = ($notes -replace '^# [^\n]*\n', '').Trim()
}
$path = Join-Path $dist 'release-manifest.json'
[IO.File]::WriteAllText($path, ($manifest | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding $false))
Write-Host "  wrote dist\$Version\release-manifest.json ($(@($files).Count) files)"
