# Release step "website": MaskedMail is not on darkciphersystems.com yet (release-config.json "website": false), so this
# step records "off". When Luke turns it on, it will build the website bundle from the staged public copy (pages and
# screenshots) plus the installers from dist\<ver>\ verified against the GitHub Release's SHA-256s, hand it to the
# Website & Deployments session's deploy (production needs Luke's go), then compare every live file byte for byte.
param([Parameter(Mandatory)][string]$Version, [switch]$DryRun)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseLib.ps1')
if (-not (Get-ReleaseConfig).website) { return New-StepResult 'off' 'MaskedMail is not on the website yet (release-config.json "website": false)' }
throw 'the website step is switched on but not built yet for MaskedMail: see docs/RELEASING.md, "Website and Discord"'
