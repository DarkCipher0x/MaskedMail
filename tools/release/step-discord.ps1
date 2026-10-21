# Release step "discord": MaskedMail has no Discord channel yet (release-config.json "discord": false), so this step
# records "off". When Luke turns it on, the card is made from the release notes and the GitHub Release's files, the
# webhook is read from Windows Credential Manager (DarkCipher/Discord/maskedmail, never a file), and a post is recorded
# in %USERPROFILE%\.darkcipher\discord-posted.json so it is never sent twice.
param([Parameter(Mandatory)][string]$Version, [switch]$DryRun)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseLib.ps1')
if (-not (Get-ReleaseConfig).discord) { return New-StepResult 'off' 'MaskedMail has no Discord channel yet (release-config.json "discord": false)' }
throw 'the Discord step is switched on but not built yet for MaskedMail: see docs/RELEASING.md, "Website and Discord"'
