# DarkCipher MaskedMail release pipeline: the one command that publishes a built version (ASCII only: Windows
# PowerShell 5.1 reads scripts as ANSI). docs/RELEASING.md explains every step.
#
#   .\build.ps1                         build dist\<ver>\ (tests, self-test, MSI, the privacy-scanned source zip)
#   .\release.ps1 -DryRun               show what every step would do; changes nothing anywhere
#   .\release.ps1                       publish the version in Directory.Build.props
#   .\release.ps1 -Version 0.3.2        publish (or finish publishing) an already-built version
#   .\release.ps1 -From release         carry on from a step, once every earlier step is recorded as complete
#   .\release.ps1 -NoPublish            run the local steps only
#
# Steps, always in this order (tools\release\step-*.ps1):
#   verify    pre-publish checks on dist\<ver>\: files, SHA256SUMS, the version everywhere, tests passed   (local)
#   sanitise  rebuild the public copy from the source zip and run the allowlist, image, privacy and gitleaks gates (local)
#   mirror    push it to the public repository as one commit and tag v<ver>                             (public)
#   release   the GitHub Release with the installers and checksums                                     (public)
#   website   darkciphersystems.com from the public copy (off when release-config.json says so)           (public)
#   discord   the announcement card (off for MaskedMail until it has a channel)                         (public)
#
# Kill switch: DCX_PUBLISH_OFF=1 (for this shell, or "setx DCX_PUBLISH_OFF 1" for every new one) holds every public step
# while the local ones still run. Every step can be run again: it finds what is already done and does only the rest.
# What happened is recorded in dist\<ver>\release-state.json.
param(
    [string]$Version,
    [switch]$DryRun,
    [switch]$NoPublish,
    [ValidateSet('verify', 'sanitise', 'mirror', 'release', 'website', 'discord')][string]$From
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'tools\release\ReleaseLib.ps1')
if (-not $Version) { $Version = Get-PropsVersion }
if (-not (Test-Path (Get-DistDir $Version))) { throw "dist\$Version does not exist: run build.ps1 first" }

$steps = @(
    @{ Name = 'verify'; Script = 'step-verify.ps1'; Public = $false },
    @{ Name = 'sanitise'; Script = 'step-sanitise.ps1'; Public = $false },
    @{ Name = 'mirror'; Script = 'step-mirror.ps1'; Public = $true },
    @{ Name = 'release'; Script = 'step-github-release.ps1'; Public = $true },
    @{ Name = 'website'; Script = 'step-website.ps1'; Public = $true },
    @{ Name = 'discord'; Script = 'step-discord.ps1'; Public = $true }
)
$complete = 'done', 'skipped', 'off'
$state = Read-ReleaseState $Version
$hold = if ($NoPublish) { '-NoPublish' } elseif (Test-PublishOff) { 'the kill switch (DCX_PUBLISH_OFF)' } else { $null }
$mode = if ($DryRun) { 'DRY RUN - nothing is changed' } else { 'live' }
Write-Host "MaskedMail $Version release pipeline ($mode)" -ForegroundColor Cyan
if ($hold) { Write-Host "Public steps are held by $hold." -ForegroundColor Yellow }

function Record([string]$step, [string]$status, [string]$detail) {
    $state[$step] = @{ status = $status; at = (Get-Date).ToString('s'); detail = $detail }
    if (-not $DryRun) { Save-ReleaseState $Version $state }
    $colour = switch ($status) { 'done' { 'Green' } 'skipped' { 'Green' } 'would-do' { 'Cyan' } 'off' { 'DarkGray' } 'held' { 'Yellow' } 'pending' { 'Yellow' } default { 'Red' } }
    Write-Host ("  {0,-9} {1,-9} {2}" -f $step, $status, $detail) -ForegroundColor $colour
}

$started = -not $From
$failed = $false
foreach ($s in $steps) {
    if (-not $started) {
        if ($s.Name -ne $From) {
            # Carrying on from a later step needs every earlier one recorded as complete for this version.
            if (-not $state.Contains($s.Name) -or $complete -notcontains $state[$s.Name].status) { throw "can't start at '$From': step '$($s.Name)' has not completed for $Version" }
            continue
        }
        $started = $true
    }
    if ($s.Public -and $hold -and -not $DryRun) { Record $s.Name 'held' "not run: held by $hold"; continue }
    try {
        $r = & (Join-Path $PSScriptRoot "tools\release\$($s.Script)") -Version $Version -DryRun:$DryRun
        $r = @($r) | Where-Object { $_ -and $_.PSObject.Properties['Status'] } | Select-Object -Last 1
        if (-not $r) { throw "step '$($s.Name)' returned no result" }
        Record $s.Name $r.Status $r.Detail
    }
    catch {
        Record $s.Name 'failed' $_.Exception.Message
        $failed = $true
        break
    }
}

if ($failed) {
    Write-Host "Stopped. Earlier steps stay as they are; fix the problem and run release.ps1 again (finished steps are recognised)." -ForegroundColor Red
    exit 1
}
if ($DryRun) { Write-Host 'Dry run finished: nothing was changed.' -ForegroundColor Cyan }
elseif ($hold) { Write-Host "Local steps finished; public steps held. Run release.ps1 again once $hold is lifted." -ForegroundColor Yellow }
else {
    $waiting = @($state.Keys | Where-Object { $state[$_].status -eq 'pending' })
    if ($waiting.Count) { Write-Host "MaskedMail $Version is out on GitHub; still pending: $($waiting -join ', '). Run release.ps1 -From $($waiting[0]) once that is done." -ForegroundColor Yellow }
    else { Write-Host "MaskedMail $Version is published everywhere. Record: dist\$Version\release-state.json" -ForegroundColor Green }
}
