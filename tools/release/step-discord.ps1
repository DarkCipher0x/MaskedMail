# Release step "discord": checks the release was announced in MaskedMail's channel on The Cipher Collective.
# The card (DarkCipherSystems-Website\tools\announcements\maskedmail-<ver>.json) is posted by the Website & Deployments
# session after the site is live, with a preview and the owner's OK first; the webhook stays in Windows Credential Manager and
# is never read here. Posted cards are recorded in %USERPROFILE%\.darkcipher\discord-posted.json, which this step reads:
#   - recorded: done;   - not yet: pending (run release.ps1 -From discord after the post).
# Off when release-config.json says "discord": false.
param([Parameter(Mandatory)][string]$Version, [switch]$DryRun)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseLib.ps1')
$cfg = Get-ReleaseConfig
if (-not $cfg.discord) { return New-StepResult 'off' 'MaskedMail has no Discord channel (release-config.json "discord": false)' }
$key = "maskedmail-$Version"
$posted = Join-Path $env:USERPROFILE '.darkcipher\discord-posted.json'
if (Test-Path $posted) {
    $p = ([IO.File]::ReadAllText($posted) | ConvertFrom-Json).$key
    if ($p) { return New-StepResult 'done' "announced in #$($p.channel) at $($p.at)" }
}
New-StepResult 'pending' "card $key not posted yet (Website & Deployments posts it after the site is live, with the owner's OK)"
