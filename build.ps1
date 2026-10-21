# DarkCipher MaskedMail release build (ASCII only: Windows PowerShell 5.1 reads scripts as ANSI).
# 1. build the whole solution (warnings are errors: .NET analyzers incl. every security rule, NuGet vulnerability
#    audit, locked package versions)   2. run the unit tests (must all pass)
# 3. publish a self-contained single-file exe   4. run its --selftest (UI + runtime checks, must pass, no windows)
# 5. build the MSI (WiX 5, pinned in .config\dotnet-tools.json)
# 6. write dist\<version>\ with the MSI, zip, the privacy-scanned source, SHA-256 checksums and release notes. Old
#    releases are never touched.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$sln = Join-Path $root 'DarkCipher.MaskedMail.sln'
$proj = Join-Path $root 'src\DcxMaskedMail\DcxMaskedMail.csproj'
# One version for every project, in Directory.Build.props.
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$dist = Join-Path $root "dist\$version"
if (Test-Path $dist) { throw "dist\$version already exists. Bump <Version> in Directory.Build.props first (never reuse a version)." }

# Scratch folder for the test logs and the self-test (never the real data folder)
$scratch = Join-Path $env:TEMP ("dcxmail-build-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $scratch | Out-Null

# Locked restore: fails if any package version drifted from the committed packages.lock.json files.
dotnet restore $sln --locked-mode --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'locked restore failed (a package version changed - review, then update the lock files)' }
dotnet build $sln -c Release --no-restore --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'solution build failed (analyzer, audit or compile error)' }
$tests = Join-Path $scratch 'tests.txt'
dotnet test $sln -c Release --no-build --nologo --logger "console;verbosity=normal" *> $tests
$testExit = $LASTEXITCODE
Get-Content $tests | Select-String -Pattern '^Test Run (Successful|Failed|Aborted)|^Total tests:|^\s+(Passed|Failed|Skipped): ' | ForEach-Object { $_.Line.Trim() }
if ($testExit -ne 0) { Get-Content $tests | Select-String -Pattern 'Failed ' | ForEach-Object { $_.Line }; throw 'unit tests failed' }

$pub = Join-Path $root 'publish'
if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
dotnet publish $proj -c Release -o $pub --nologo
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

$exe = Join-Path $pub 'DcxMaskedMail.exe'
if (Get-ChildItem $pub -Filter *.pdb) { throw 'a .pdb file is in the publish folder (local paths would ship)' }

# Self-test against the published exe, in a scratch data folder
$report = Join-Path $scratch 'selftest.txt'
$env:DCX_DATA_DIR = Join-Path $scratch 'data'
$p = Start-Process $exe -ArgumentList '--selftest', "`"$report`"" -Wait -PassThru
Remove-Item Env:\DCX_DATA_DIR
if (-not (Test-Path $report)) { throw "self-test wrote no report (exit $($p.ExitCode))" }
Get-Content $report -Encoding UTF8 | Select-Object -Last 3
if ($p.ExitCode -ne 0) { Get-Content $report -Encoding UTF8; throw 'self-test failed' }

New-Item -ItemType Directory -Path $dist | Out-Null

# The README's screenshots (docs\screenshots, shown on GitHub) come from this build's renders, so they always match the
# release. Demo data only; look at each one before publishing. The README's version badge follows the version too.
$shotDir = Join-Path $root 'docs\screenshots'
if (Test-Path $shotDir) { Remove-Item (Join-Path $shotDir '*.png') }
else { New-Item -ItemType Directory -Path $shotDir | Out-Null }
foreach ($s in 'main-dark', 'main-light', 'main-throwaway', 'throwaway', 'new-alias', 'compose', 'queue', 'sign-in', 'settings', 'settings-masked', 'settings-domains', 'settings-disposable', 'settings-throwaway') {
    $src = Join-Path $scratch "shots\$s.png"
    if (-not (Test-Path $src)) { throw "missing self-test render for the README: $s.png" }
    Copy-Item $src $shotDir
}
$readme = Join-Path $root 'README.md'
$readmeText = [IO.File]::ReadAllText($readme, [Text.Encoding]::UTF8)
$readmeText = [regex]::Replace($readmeText, 'badge/version-[0-9.]+-', "badge/version-$version-")
$readmeText = [regex]::Replace($readmeText, 'alt="Version [0-9.]+"', "alt=""Version $version""")
[IO.File]::WriteAllText($readme, $readmeText, (New-Object Text.UTF8Encoding $false))

# MSI: Start menu + optional desktop shortcut, upgrades older versions, optional data removal on uninstall
Push-Location $root
try {
    dotnet tool restore | Out-Null
    dotnet tool run wix extension add -g WixToolset.UI.wixext/5.0.2 | Out-Null
    $msi = Join-Path $dist "DarkCipherMaskedMail-$version-x64.msi"
    dotnet tool run wix build installer\DarkCipherMaskedMail.wxs -arch x64 -ext WixToolset.UI.wixext `
        -d Version=$version -d PublishDir="$pub" -wx -o $msi
    if ($LASTEXITCODE) { throw 'wix build failed' }
    Remove-Item (Join-Path $dist '*.wixpdb') -ErrorAction SilentlyContinue
} finally { Pop-Location }

$zip = Join-Path $dist "DarkCipher-MaskedMail-$version-win-x64.zip"
$stage = Join-Path $scratch 'stage'
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item $exe $stage
Copy-Item (Join-Path $root 'README.md') $stage
Copy-Item (Join-Path $root 'LICENSE') $stage
# CHANGELOG.md is generated from docs\release-notes\*.md (newest first), so it is never out of date.
$notesDir = Join-Path $root 'docs\release-notes'
$all = Get-ChildItem $notesDir -Filter '*.md' | Sort-Object { [version]$_.BaseName } -Descending
if (-not ($all | Where-Object BaseName -eq $version)) { throw "missing release notes: docs\release-notes\$version.md" }
$log = New-Object Text.StringBuilder
[void]$log.AppendLine('# DarkCipher MaskedMail - changelog').AppendLine().AppendLine('Generated by build.ps1 from docs/release-notes. Newest first.').AppendLine()
foreach ($n in $all) {
    $text = [IO.File]::ReadAllText($n.FullName, [Text.Encoding]::UTF8).TrimStart([char]0xFEFF).Replace('](../', '](')
    $text = [regex]::Replace($text, '(?m)^# ', '## ')
    [void]$log.AppendLine($text.Trim()).AppendLine()
}
[IO.File]::WriteAllText((Join-Path $root 'docs\CHANGELOG.md'), $log.ToString(), (New-Object Text.UTF8Encoding $false))

# Documentation travels with every release: user guide, security model, the latest security audit, privacy policy,
# add-ons, architecture, testing and the changelog.
$docNames = 'USER-GUIDE.md', 'SECURITY.md', 'PRIVACY.md', 'ADDONS.md', 'ARCHITECTURE.md', 'TESTING.md', 'CHANGELOG.md'
$audit = Get-ChildItem (Join-Path $root 'docs') -Filter 'SECURITY-AUDIT-*.md' | Sort-Object Name | Select-Object -Last 1
New-Item -ItemType Directory -Path (Join-Path $stage 'docs') | Out-Null
New-Item -ItemType Directory -Path (Join-Path $dist 'docs') | Out-Null
foreach ($d in $docNames) {
    $p = Join-Path $root "docs\$d"
    if (-not (Test-Path $p)) { throw "missing documentation: docs\$d" }
    Copy-Item $p (Join-Path $stage 'docs'); Copy-Item $p (Join-Path $dist 'docs')
}
if ($audit) { Copy-Item $audit.FullName (Join-Path $stage 'docs'); Copy-Item $audit.FullName (Join-Path $dist 'docs') }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Copy-Item $exe (Join-Path $dist 'DcxMaskedMail.exe')
$notes = Join-Path $root "docs\release-notes\$version.md"
if (Test-Path $notes) { Copy-Item $notes (Join-Path $dist "RELEASE-NOTES-$version.md") }
Copy-Item $report (Join-Path $dist "selftest-$version.txt")
# Only the test names and results ship (the raw log has local paths in it).
$testLines = @("DarkCipher MaskedMail $version unit tests") + @(Get-Content $tests | Where-Object { $_ -match '^\s+(Passed|Failed|Skipped) DcxMail\.' -or $_ -match '^Test Run (Successful|Failed|Aborted)|^Total tests:|^\s+(Passed|Failed|Skipped): ' } | ForEach-Object { $_.Trim() })
if (($testLines -join "`n") -match [regex]::Escape($env:USERNAME)) { throw 'the test summary contains the user name' }
[IO.File]::WriteAllLines((Join-Path $dist "tests-$version.txt"), [string[]]$testLines)

# Microsoft Store kit: submission guide, privacy policy and Store-size screenshots (self-test renders, demo data only;
# look at each one before uploading). The Store wants at least 1366 x 768.
$store = Join-Path $dist 'store'
New-Item -ItemType Directory -Path (Join-Path $store 'screenshots') | Out-Null
Copy-Item (Join-Path $root 'installer\msix\STORE-SUBMISSION.md') $store
Copy-Item (Join-Path $root 'docs\PRIVACY.md') $store
Add-Type -AssemblyName System.Drawing
$shotNo = 0
foreach ($s in 'main-dark', 'main-throwaway', 'main-light', 'settings') {
    $src = Join-Path $scratch "shots\$s.png"
    if (-not (Test-Path $src)) { throw "missing self-test render for the Store: $s.png" }
    $img = [System.Drawing.Image]::FromFile($src); $w = $img.Width; $h = $img.Height; $img.Dispose()
    if ($w -lt 1366 -or $h -lt 768) { throw "Store screenshot $s.png is ${w}x${h}; the Store needs at least 1366x768" }
    $shotNo++
    Copy-Item $src (Join-Path $store ("screenshots\{0:00}-{1}.png" -f $shotNo, $s))
}

# The source this release was built from, privacy-scanned (stops the build on a local path, private address or key).
& (Join-Path $root 'tools\write-public-source.ps1') -Version $version

$sums = Get-ChildItem $dist -File -Recurse | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.FullName.Substring($dist.Length + 1).Replace('\', '/')
}
[IO.File]::WriteAllLines((Join-Path $dist 'SHA256SUMS.txt'), $sums)
Remove-Item $scratch -Recurse -Force

# MSIX for the Microsoft Store: the Store package once installer\msix\store-identity.json is filled in from Partner
# Center; until then a validation pack (-Check), so a broken manifest never goes unnoticed.
$identity = [IO.File]::ReadAllText((Join-Path $root 'installer\msix\store-identity.json')) | ConvertFrom-Json
if ($identity.IdentityName -and $identity.Publisher -and $identity.PublisherDisplayName) {
    & (Join-Path $root 'tools\make-msix.ps1')
} else {
    & (Join-Path $root 'tools\make-msix.ps1') -Check
    Write-Host 'Store identity not filled in yet: the MSIX was validated, not written (installer\msix\STORE-SUBMISSION.md).' -ForegroundColor Yellow
}
Write-Host "Release $version written to $dist" -ForegroundColor Green
Write-Host 'Next: view any new screenshots (approve them in tools\approved-images.txt), then .\release.ps1 -DryRun and .\release.ps1 (docs\RELEASING.md).'
