# Release step "website": checks that darkciphersystems.com serves exactly this release.
# The website is built and deployed by the Website & Deployments session (production deploys need the owner's go), from
# dist\<ver>\release-manifest.json, which the release step writes. This step never changes the site. It downloads every
# file in the manifest from its site_path and compares the SHA-256 byte for byte:
#   - all equal: done;
#   - not there yet (404): pending, which is normal until the site is deployed (run release.ps1 -From website later);
#   - any file different: failed, a mismatch is a failed publish.
# Off when release-config.json says "website": false.
param([Parameter(Mandatory)][string]$Version, [switch]$DryRun)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseLib.ps1')
$cfg = Get-ReleaseConfig
if (-not $cfg.website) { return New-StepResult 'off' 'MaskedMail is not on the website (release-config.json "website": false)' }
$manifestPath = Join-Path (Get-DistDir $Version) 'release-manifest.json'
if (-not (Test-Path $manifestPath)) { throw "no release-manifest.json for ${Version}: run the release step first" }
$manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('dcxmail-site-' + [guid]::NewGuid().ToString('N'))
$missing = @(); $same = 0
try {
    foreach ($f in @($manifest.files)) {
        $url = 'https://darkciphersystems.com/' + $f.site_path
        try { Invoke-WebRequest $url -OutFile $tmp -UseBasicParsing -MaximumRedirection 0 -TimeoutSec 600 }
        catch {
            if ($_.Exception.Response -and $_.Exception.Response.StatusCode.value__ -eq 404) { $missing += $f.site_path; continue }
            throw "could not fetch ${url}: $($_.Exception.Message)"
        }
        $got = (Get-FileHash $tmp -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($got -ne $f.sha256) { throw "the website's $($f.site_path) differs from release $Version (SHA-256 $got, expected $($f.sha256)): failed publish" }
        $same++
    }
}
finally { Remove-Item $tmp -ErrorAction SilentlyContinue }
if ($missing.Count) { return New-StepResult 'pending' "not on the website yet: $($missing -join ', ') ($same of $(@($manifest.files).Count) live and identical). Website & Deployments deploys it; then run release.ps1 -From website" }
New-StepResult 'done' "all $same files on darkciphersystems.com are byte-for-byte identical to release $Version"
