# Release step "mirror": makes the public repository's clone (release-config.json: publicClone) exactly the staged
# public copy, as one commit "MaskedMail <ver> - sanitised public source" and an annotated tag v<ver>, authored as
# DarkCipher0x with the GitHub no-reply address, and pushes both. Never --force.
#   - The clone's origin must be exactly release-config.json's publicRemote, on its branch, with no local changes.
#   - A version is published once: if v<ver> exists with the same content the step is already done (skipped); with
#     different content it stops, and the fix is a new version.
#   - If an earlier run committed and tagged but the push failed, this run pushes them.
#   - Dry run: the same work in a throwaway local copy of the clone, then "git push --dry-run".
param([Parameter(Mandatory)][string]$Version, [switch]$DryRun)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseLib.ps1')
$cfg = Get-ReleaseConfig
$clone = [IO.Path]::GetFullPath((Join-Path $script:ProjectRoot $cfg.publicClone))
$top = Join-Path (Get-StagingDir $Version) (Get-SourceName $Version)
if (-not (Test-Path $top)) { throw "no public copy staged for ${Version}: run the sanitise step first" }
$tag = "v$Version"
$script:repo = $clone

function G { & git -C $script:repo @args | Out-Host; if ($LASTEXITCODE) { throw "git $($args -join ' ') failed" } }
function GOut { $o = & git -C $script:repo @args; if ($LASTEXITCODE) { throw "git $($args -join ' ') failed" }; $o }

# 1. The clone: the right remote, the right branch, nothing uncommitted.
if (-not (Test-Path (Join-Path $clone '.git'))) { throw "no public clone at ${clone}: git clone $($cfg.publicRemote) there first" }
$origin = GOut remote get-url origin
if ($origin -ne $cfg.publicRemote) { throw "the clone's origin is '$origin', not '$($cfg.publicRemote)': refusing to push anywhere else" }
if ((GOut branch --show-current) -ne $cfg.branch) { throw "the clone is not on $($cfg.branch)" }
if (GOut status --porcelain) { throw "the clone $clone has local changes: commit or discard them first" }

$work = $null
if ($DryRun) {
    $work = Join-Path ([IO.Path]::GetTempPath()) ('dcxmail-mirror-dry-' + [guid]::NewGuid().ToString('N'))
    & git clone -q --no-hardlinks $clone $work | Out-Host
    if ($LASTEXITCODE) { throw 'could not make the dry-run copy of the clone' }
    $script:repo = $work
    G remote set-url origin $cfg.publicRemote
}
$push = @('push', '-q') + $(if ($DryRun) { @('--dry-run') } else { @() })
try {
    # 2. Up to date with GitHub, and is this version there already?
    G fetch -q origin $cfg.branch
    G merge -q --ff-only "origin/$($cfg.branch)"
    $remoteTag = GOut ls-remote --tags origin "refs/tags/$tag"
    $localTag = GOut tag -l $tag

    # 3. The tree becomes exactly the staged copy.
    Get-ChildItem $script:repo -Force | Where-Object Name -ne '.git' | Remove-Item -Recurse -Force
    Get-ChildItem $top -Force | Copy-Item -Destination $script:repo -Recurse -Force
    G -c core.safecrlf=false add -A

    if ($localTag -or $remoteTag) {
        if (-not $localTag) { G fetch -q origin "refs/tags/${tag}:refs/tags/$tag" }
        & git -C $script:repo diff --cached --quiet $tag
        $same = ($LASTEXITCODE -eq 0)
        G reset -q --hard HEAD
        G clean -q -fd
        if (-not $same) { throw "$tag is already published with different content. A published version is never changed: release a new version." }
        if ($remoteTag) { return New-StepResult 'skipped' "$tag is already on GitHub with exactly this content" }
        G @push origin $cfg.branch
        G @push origin $tag
        return New-StepResult $(if ($DryRun) { 'would-do' } else { 'done' }) "pushed $tag, committed by an earlier run"
    }

    # 4. One commit and one annotated tag, then the push.
    if (GOut status --porcelain) { G @script:Identity commit -q -m "MaskedMail $Version - sanitised public source" }
    G @script:Identity tag -a $tag -m "MaskedMail $Version"
    $changed = @(GOut diff --name-only "HEAD~1" HEAD).Count
    $commit = (GOut rev-parse --short HEAD)
    G @push origin $cfg.branch
    G @push origin $tag
    if ($DryRun) { return New-StepResult 'would-do' "would push commit $commit ($changed files changed) and tag $tag to $($cfg.publicRemote)" }
    New-StepResult 'done' "pushed commit $commit ($changed files changed) and tag $tag to $($cfg.publicRemote)"
}
finally {
    if ($work) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
}
