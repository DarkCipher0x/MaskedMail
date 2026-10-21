# Release step "verify": the pre-publish checks on dist\<ver>\. It only reads, so it is always safe to run again.
#   - every expected file is there (MSI, zip, exe, source zip, notes, checksums, test and self-test reports);
#   - SHA256SUMS.txt verifies, and lists every file in the folder;
#   - the version agrees everywhere: the exe, the MSI, the source's Directory.Build.props, its README badge, the notes;
#   - the build passed: every unit test passed and the self-test has no failures.
param([Parameter(Mandatory)][string]$Version, [switch]$DryRun)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseLib.ps1')
$dist = Get-DistDir $Version
if (-not (Test-Path $dist)) { throw "dist\$Version does not exist: build it first (build.ps1)" }
$name = Get-SourceName $Version
$need = "DarkCipherMaskedMail-$Version-x64.msi", "DarkCipher-MaskedMail-$Version-win-x64.zip", 'DcxMaskedMail.exe', "$name.zip",
    'SHA256SUMS.txt', "tests-$Version.txt", "selftest-$Version.txt", "RELEASE-NOTES-$Version.md"
$missing = @($need | Where-Object { -not (Test-Path (Join-Path $dist $_)) })
if ($missing.Count) { throw "missing from dist\${Version}: $($missing -join ', ')" }

# Checksums: every line verifies, and every file is listed (only this pipeline's own record and manifest may be unlisted).
$listed = @{}
foreach ($line in [IO.File]::ReadAllLines((Join-Path $dist 'SHA256SUMS.txt'))) {
    if (-not $line.Trim()) { continue }
    if ($line -notmatch '^([0-9a-f]{64})  (.+)$') { throw "SHA256SUMS.txt: unreadable line '$line'" }
    $want = $Matches[1]; $file = $Matches[2]
    $path = Join-Path $dist $file.Replace('/', '\')
    if (-not (Test-Path $path)) { throw "SHA256SUMS.txt lists $file, which is missing" }
    if ((Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $want) { throw "checksum mismatch: $file" }
    $listed[$file] = $true
}
$unlisted = @(Get-ChildItem $dist -Recurse -File | ForEach-Object { $_.FullName.Substring($dist.Length + 1).Replace('\', '/') } |
    Where-Object { $_ -ne 'SHA256SUMS.txt' -and $_ -ne 'release-state.json' -and $_ -ne 'release-manifest.json' -and -not $listed.ContainsKey($_) })
if ($unlisted.Count) { throw "files in dist\$Version that SHA256SUMS.txt does not cover: $($unlisted -join ', ')" }

# The version, everywhere it is written.
$exeVersion = (Get-Item (Join-Path $dist 'DcxMaskedMail.exe')).VersionInfo.ProductVersion
if ($exeVersion -notlike "$Version*") { throw "the exe says $exeVersion, not $Version" }
$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @([string](Join-Path $dist "DarkCipherMaskedMail-$Version-x64.msi"), [int]0))
$view = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, @("SELECT Value FROM Property WHERE Property='ProductVersion'"))
[void]$view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
$record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
$msiVersion = $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, @(1))
[void]$view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null)
[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($db)
if ($msiVersion -ne $Version) { throw "the MSI says $msiVersion, not $Version" }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $dist "$name.zip"))
try {
    function Read-Entry([string]$path) {
        $e = $zip.GetEntry("$name/$path")
        if (-not $e) { throw "the source zip has no $path (expected under $name/)" }
        $r = New-Object IO.StreamReader($e.Open()); try { $r.ReadToEnd() } finally { $r.Dispose() }
    }
    if ((Read-Entry 'Directory.Build.props') -notmatch "<Version>$([regex]::Escape($Version))</Version>") { throw "the source's Directory.Build.props is not $Version" }
    $readme = Read-Entry 'README.md'
    if ($readme -match 'badge/version-([0-9.]+)-' -and $Matches[1] -ne $Version) { throw "the README badge says $($Matches[1]), not $Version" }
    if ((Read-Entry "docs/release-notes/$Version.md") -notmatch "^# DarkCipher MaskedMail $([regex]::Escape($Version)) ") { throw "docs/release-notes/$Version.md does not start with '# DarkCipher MaskedMail $Version'" }
}
finally { $zip.Dispose() }

# The build passed its own tests.
$tests = [IO.File]::ReadAllText((Join-Path $dist "tests-$Version.txt"))
if ($tests -notmatch 'Total tests: (\d+)') { throw "tests-$Version.txt has no total" }
$total = [int]$Matches[1]
if ($tests -notmatch '(?m)^Passed: (\d+)' -or [int]$Matches[1] -ne $total -or $tests -match '(?m)^Failed: [1-9]') { throw "tests-$Version.txt: not every one of the $total tests passed" }
$self = [IO.File]::ReadAllText((Join-Path $dist "selftest-$Version.txt"))
if ($self -notmatch '(\d+) ok, 0 failed') { throw "selftest-$Version.txt does not say '0 failed'" }

New-StepResult 'done' "$($listed.Count) files match SHA256SUMS; exe, MSI, source and notes all say $Version; $total/$total tests and $($Matches[1]) self-test checks passed"
