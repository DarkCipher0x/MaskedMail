# Shared helpers for MaskedMail's release scripts, dot-sourced by each of them (ASCII only: Windows PowerShell 5.1 reads
# scripts as ANSI). Nothing here publishes anything; the step scripts do, one job each.
$script:ReleaseDir = $PSScriptRoot
$script:ToolsDir = Split-Path $PSScriptRoot -Parent
$script:ProjectRoot = Split-Path $script:ToolsDir -Parent
$script:GitleaksVersion = '8.30.1'
$script:GitleaksExe = Join-Path $script:ToolsDir "bin\gitleaks-$($script:GitleaksVersion)\gitleaks.exe"
$script:Identity = @('-c', 'user.name=DarkCipher0x', '-c', 'user.email=DarkCipher0x@users.noreply.github.com')

function Get-ReleaseConfig { [IO.File]::ReadAllText((Join-Path $script:ToolsDir 'release-config.json')) | ConvertFrom-Json }

function Get-PropsVersion {
    ([xml](Get-Content (Join-Path $script:ProjectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}

function Get-DistDir([string]$Version) { Join-Path $script:ProjectRoot "dist\$Version" }
function Get-SourceName([string]$Version) { "DarkCipherMaskedMail-$Version-source" }
function Get-StagingDir([string]$Version) { Join-Path ([IO.Path]::GetTempPath()) "dcxmail-public-$Version" }

# The kill switch: DCX_PUBLISH_OFF=1 (this process, or set for the user with setx) holds every public step.
function Test-PublishOff {
    $v = $env:DCX_PUBLISH_OFF
    if (-not $v) { $v = [Environment]::GetEnvironmentVariable('DCX_PUBLISH_OFF', 'User') }
    return [bool]($v -and $v -ne '0')
}

# ---------- the allowlist ----------

# One glob per line ('*' within a folder, '**' any folders), '#' comments. Returns anchored, case-insensitive regexes.
function Read-PatternFile([string]$name) {
    $path = Join-Path $script:ToolsDir $name
    if (-not (Test-Path $path)) { throw "tools\$name is missing" }
    foreach ($line in [IO.File]::ReadAllLines($path)) {
        $p = $line.Trim()
        if (-not $p -or $p.StartsWith('#')) { continue }
        $rx = [regex]::Escape($p).Replace('\*\*/', '(?:.*/)?').Replace('\*\*', '.*').Replace('\*', '[^/]*').Replace('\?', '[^/]')
        '(?i)^' + $rx + '$'
    }
}

function Test-AnyPattern([string]$rel, $patterns) {
    foreach ($rx in $patterns) { if ($rel -match $rx) { return $true } }
    return $false
}

# Splits a file list into public files, private files and files nobody has classified yet.
function Split-ByAllowlist([string[]]$files) {
    $allow = @(Read-PatternFile 'public-allowlist.txt')
    $deny = @(Read-PatternFile 'private-list.txt')
    $result = @{ Public = New-Object System.Collections.Generic.List[string]; Private = New-Object System.Collections.Generic.List[string]; Unclassified = New-Object System.Collections.Generic.List[string] }
    foreach ($f in $files) {
        if (Test-AnyPattern $f $deny) { $result.Private.Add($f) }
        elseif (Test-AnyPattern $f $allow) { $result.Public.Add($f) }
        else { $result.Unclassified.Add($f) }
    }
    return $result
}

# Every file under a folder as '/'-separated relative paths, skipping build output.
function Get-TreeFiles([string]$dir) {
    $skip = '^(dist|publish|\.vs|\.git|\.selftest[^/]*|TestResults)/|(^|/)(bin|obj)/'
    Get-ChildItem $dir -Recurse -File -Force | ForEach-Object { $_.FullName.Substring($dir.Length + 1).Replace('\', '/') } |
        Where-Object { $_ -notmatch $skip } | Sort-Object
}

# ---------- the checks every public file goes through ----------

# Local paths, private e-mail addresses, keys, the private terms (tools\privacy-terms.local.txt, never published) and the
# Windows user name. Keys and paths in the tests and fakes are synthetic fixtures, so those rules skip them; the terms
# apply everywhere. Binaries are checked for paths and the user name only (a DLL can carry its build path).
function Invoke-PrivacyScan([string]$dir, [string[]]$files) {
    $rules = @(
        @{ Name = 'a local user path'; Rx = '(?i)[A-Z]:\\Users\\(?!Public\\|demo\\|<|you\\|%)[^\\\s"'']+'; SkipTests = $true },
        @{ Name = 'a private e-mail address'; Rx = '(?i)\b[A-Z0-9._%+-]+@(?!example\.|[a-z0-9.-]*\.(test|example|invalid|local|lan)\b|darkciphersystems\.com|anthropic\.com|users\.noreply\.github\.com|[a-z0-9.-]*addy\.io|simplelogin\.com|duck\.com|mozmail\.com)[A-Z0-9.-]+\.[A-Z]{2,}\b'; SkipTests = $true },
        @{ Name = 'an API key or token'; Rx = '\b(sk-[A-Za-z0-9]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|fmu1-[A-Za-z0-9-]{20,}|AKIA[0-9A-Z]{16})\b'; SkipTests = $true },
        @{ Name = 'a Discord webhook'; Rx = '(?i)discord(app)?\.com/api/webhooks/\d+'; SkipTests = $false },
        @{ Name = 'a private key'; Rx = '-----BEGIN [A-Z ]*PRIVATE KEY-----'; SkipTests = $false }
    )
    $termsFile = Join-Path $script:ToolsDir 'privacy-terms.local.txt'
    if (-not (Test-Path $termsFile)) { throw 'tools\privacy-terms.local.txt not found: the private names must be checked before anything is published' }
    $terms = @([IO.File]::ReadAllLines($termsFile) | Where-Object { $_.Trim().Length -gt 0 -and -not $_.StartsWith('#') } | ForEach-Object { $_.Trim() })
    $termRx = @($terms | ForEach-Object { if ($_ -match '^\w+$') { '(?i)\b' + [regex]::Escape($_) + '\b' } else { '(?i)' + [regex]::Escape($_) } })
    $termRx += '(?i)\b' + [regex]::Escape($env:USERNAME) + '\b'
    $binExt = '.png', '.ico', '.dll', '.rtf', '.zip', '.exe'
    $hits = New-Object System.Collections.Generic.List[string]
    foreach ($f in $files) {
        $path = Join-Path $dir $f
        $isTest = $f -match '^tests/|^src/DcxMail\.Fakes/|/SelfTest\.cs$'
        if ([IO.Path]::GetExtension($f).ToLowerInvariant() -in $binExt) {
            $raw = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($path))
            if ($raw -match '(?i)[A-Z]:\\Users\\(?!Public\\)[^\\\x00]+' -or $raw -match ('(?i)' + [regex]::Escape($env:USERNAME))) { $hits.Add("${f}: a local path or user name inside a binary") }
            continue
        }
        $lines = [IO.File]::ReadAllLines($path)
        for ($i = 0; $i -lt $lines.Count; $i++) {
            $l = $lines[$i]
            foreach ($r in $rules) { if (-not ($isTest -and $r.SkipTests) -and $l -match $r.Rx) { $hits.Add("$f($($i + 1)): $($r.Name)") } }
            foreach ($t in $termRx) { if ($l -match $t) { $hits.Add("$f($($i + 1)): a private term") } }
        }
    }
    return $hits.ToArray()
}

# gitleaks (pinned, hash-checked by get-gitleaks.ps1) with its own rules plus tools\gitleaks.toml. Returns the findings.
function Invoke-Gitleaks([string]$dir) {
    if (-not (Test-Path $script:GitleaksExe)) { throw "gitleaks $($script:GitleaksVersion) is not installed: run tools\release\get-gitleaks.ps1 once" }
    $report = Join-Path ([IO.Path]::GetTempPath()) ('dcxmail-gitleaks-' + [guid]::NewGuid().ToString('N') + '.json')
    $log = "$report.log"
    $glArgs = @('dir', "`"$dir`"", '--config', "`"$(Join-Path $script:ToolsDir 'gitleaks.toml')`"", '--no-banner', '--redact', '--exit-code', '3',
        '--report-format', 'json', '--report-path', "`"$report`"")
    $p = Start-Process $script:GitleaksExe -ArgumentList $glArgs -NoNewWindow -Wait -PassThru -RedirectStandardError $log -RedirectStandardOutput "$log.out"
    try {
        if ($p.ExitCode -eq 0) { return @() }
        if ($p.ExitCode -ne 3) { throw "gitleaks failed (exit $($p.ExitCode)): " + ([IO.File]::ReadAllText($log).Trim()) }
        $found = [IO.File]::ReadAllText($report) | ConvertFrom-Json
        return @($found | ForEach-Object { $_ } | ForEach-Object { "{0}({1}): gitleaks {2}" -f $_.File.Substring($dir.Length).TrimStart('\', '/').Replace('\', '/'), $_.StartLine, $_.RuleID })
    }
    finally { Remove-Item $report, $log, "$log.out" -ErrorAction SilentlyContinue }
}

# Every image must be listed by SHA-256 in tools\approved-images.txt: someone has looked at it (scans can't see pixels).
function Get-UnapprovedImages([string]$dir, [string[]]$files) {
    $approved = @([IO.File]::ReadAllLines((Join-Path $script:ToolsDir 'approved-images.txt')) | Where-Object { $_ -match '^[0-9a-f]{64}\s' } | ForEach-Object { $_.Substring(0, 64) })
    $files | Where-Object { $_ -match '\.(png|jpe?g|gif|bmp|ico|svg|webp|tiff?)$' } |
        Where-Object { $approved -notcontains (Get-FileHash (Join-Path $dir $_) -Algorithm SHA256).Hash.ToLowerInvariant() }
}

# ---------- GitHub ----------

# The sign-in Git Credential Manager already holds for github.com, in memory only (never printed or stored). The request
# goes in through a file redirect: Windows PowerShell's pipes start with a byte-order mark, which git rejects.
function Get-GitHubToken {
    $req = Join-Path ([IO.Path]::GetTempPath()) ('dcxmail-cred-' + [guid]::NewGuid().ToString('N') + '.txt')
    [IO.File]::WriteAllBytes($req, [Text.Encoding]::ASCII.GetBytes("protocol=https`nhost=github.com`n`n"))
    try { $answer = cmd /c "git credential fill < `"$req`"" }
    finally { Remove-Item $req -ErrorAction SilentlyContinue }
    $line = $answer | Where-Object { $_ -like 'password=*' } | Select-Object -First 1
    if (-not $line) { throw 'No GitHub sign-in in Git Credential Manager: run "git -C ..\MaskedMail-public fetch" once to sign in' }
    $line.Substring(9).Trim()
}

function Get-GitHubHeaders {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    @{ Authorization = "Bearer $(Get-GitHubToken)"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'MaskedMail-release' }
}

# GET that returns $null on 404 instead of throwing.
function Get-GitHubOrNull([string]$uri, $headers) {
    try { return Invoke-RestMethod $uri -Headers $headers }
    catch { if ($_.Exception.Response -and $_.Exception.Response.StatusCode.value__ -eq 404) { return $null }; throw }
}

# ---------- the release record (dist\<ver>\release-state.json) ----------

function Read-ReleaseState([string]$Version) {
    $path = Join-Path (Get-DistDir $Version) 'release-state.json'
    $state = [ordered]@{}
    if (Test-Path $path) {
        $o = [IO.File]::ReadAllText($path) | ConvertFrom-Json
        foreach ($p in $o.steps.PSObject.Properties) { $state[$p.Name] = @{ status = $p.Value.status; at = $p.Value.at; detail = $p.Value.detail } }
    }
    return $state
}

function Save-ReleaseState([string]$Version, $state) {
    $path = Join-Path (Get-DistDir $Version) 'release-state.json'
    $doc = [ordered]@{ app = 'MaskedMail'; version = $Version; updated = (Get-Date).ToString('s'); steps = $state }
    [IO.File]::WriteAllText($path, ($doc | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding $false))
}

function New-StepResult([string]$Status, [string]$Detail) { [pscustomobject]@{ Status = $Status; Detail = $Detail } }
