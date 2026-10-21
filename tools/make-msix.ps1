# Packages the already-published app (.\publish, from build.ps1) as an MSIX for the Microsoft Store.
# The Store signs MSIX packages for free after certification, so the Store package is uploaded UNSIGNED.
#   .\tools\make-msix.ps1             -> dist\<ver>\DarkCipherMaskedMail-<ver>-x64.msix   (Store upload; needs
#                                        installer\msix\store-identity.json filled in from Partner Center)
#   .\tools\make-msix.ps1 -TestSign   -> dist\<ver>\DarkCipherMaskedMail-<ver>-x64-test.msix plus its .cer: signed with a
#                                        self-signed certificate for installing on THIS PC only (never publish it)
#   .\tools\make-msix.ps1 -Check      -> packs with the test identity into a temp folder (makeappx schema validation),
#                                        then deletes it: signs nothing, writes nothing to dist or the certificate store
# build.ps1 runs -Check (or the Store package once the identity is filled in) after the MSI. ASCII only (PS 5.1).
param([switch]$TestSign, [switch]$Check)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
[xml]$x = Get-Content 'Directory.Build.props'
$version = $x.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$out = "dist\$version"
if (-not (Test-Path 'publish\DcxMaskedMail.exe')) { throw 'no .\publish output - run build.ps1 first' }
if ((Get-Item 'publish\DcxMaskedMail.exe').VersionInfo.ProductVersion -notlike "$version*") { throw ".\publish is not version $version - run build.ps1 again" }
if (-not $Check -and -not (Test-Path "$out\DarkCipherMaskedMail-$version-x64.msi")) { throw "dist\$version has no MSI - run build.ps1 for $version first" }

# Identity: the Store package must match Partner Center exactly; a test package uses a local-only identity.
$id = Get-Content 'installer\msix\store-identity.json' -Raw -Encoding UTF8 | ConvertFrom-Json
$storeReady = $id.IdentityName -and $id.Publisher -and $id.PublisherDisplayName
if ($TestSign -or $Check) {
    $identity = @{ IdentityName = 'DarkCipherSystems.MaskedMail.Test'; Publisher = 'CN=DarkCipher Systems Local Test'; PublisherDisplayName = 'DarkCipher Systems (local test)' }
    $name = "DarkCipherMaskedMail-$version-x64-test.msix"
} elseif ($storeReady) {
    if ($id.Publisher -notmatch '^CN=') { throw 'store-identity.json: Publisher must be the full "CN=..." value from Partner Center' }
    $identity = @{ IdentityName = $id.IdentityName; Publisher = $id.Publisher; PublisherDisplayName = $id.PublisherDisplayName }
    $name = "DarkCipherMaskedMail-$version-x64.msix"
} else {
    throw 'installer\msix\store-identity.json is not filled in yet (Partner Center -> Product identity). Use -Check to validate or -TestSign for a local test package.'
}
$pkg = if ($Check) { Join-Path ([IO.Path]::GetTempPath()) ('dcxmail-msix-check-' + [guid]::NewGuid().ToString('N') + '.msix') } else { Join-Path $out $name }
if (Test-Path $pkg) { throw "$pkg already exists - every build is a new version" }

# Windows SDK build tools, pinned (tools\msix\MsixTools.csproj), from nuget.org.
dotnet restore tools\msix\MsixTools.csproj -nologo -v q
if ($LASTEXITCODE) { throw 'restoring the Windows SDK build tools failed' }
$sdk = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools'
$makeappx = Get-ChildItem $sdk -Recurse -Filter makeappx.exe | Where-Object FullName -match '\\x64\\' | Sort-Object FullName -Descending | Select-Object -First 1
$signtool = Get-ChildItem $sdk -Recurse -Filter signtool.exe | Where-Object FullName -match '\\x64\\' | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $makeappx) { throw 'makeappx.exe not found in the SDK build tools package' }

# Stage: the published app + manifest + tile images (drawn from the app logo).
$stage = Join-Path ([IO.Path]::GetTempPath()) ('dcxmail-msix-' + [guid]::NewGuid().ToString('N'))
Copy-Item publish $stage -Recurse
$manifest = [IO.File]::ReadAllText("$root\installer\msix\AppxManifest.xml")
$tokens = $identity + @{ Version = "$version.0" }
foreach ($k in $tokens.Keys) { $manifest = $manifest.Replace("`$($k)", [Security.SecurityElement]::Escape($tokens[$k])) }
if ($manifest -match '\$\(\w+\)') { throw "unfilled manifest token: $($Matches[0])" }
[IO.File]::WriteAllText("$stage\AppxManifest.xml", $manifest, (New-Object Text.UTF8Encoding $false))

Add-Type -AssemblyName System.Drawing
$logo = [System.Drawing.Image]::FromFile("$root\src\DcxMaskedMail\Assets\logo.png")
New-Item -ItemType Directory "$stage\Images" | Out-Null
foreach ($img in @(@('StoreLogo', 50, 50), @('Square44x44Logo', 44, 44), @('Square150x150Logo', 150, 150), @('Wide310x150Logo', 310, 150))) {
    $bmp = New-Object System.Drawing.Bitmap $img[1], $img[2]
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
    $s = [Math]::Min($img[1], $img[2]) * $(if ($img[1] -ge 150) { 0.8 } else { 1.0 })
    $g.DrawImage($logo, [float](($img[1] - $s) / 2), [float](($img[2] - $s) / 2), [float]$s, [float]$s)
    $g.Dispose(); $bmp.Save("$stage\Images\$($img[0]).png", [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}
$logo.Dispose()

# Pack (makeappx validates the manifest against the schema).
& $makeappx.FullName pack /d $stage /p $pkg /o | Out-Null
$packed = -not $LASTEXITCODE
Remove-Item $stage -Recurse -Force
if (-not $packed) { if (Test-Path $pkg) { Remove-Item $pkg }; throw 'makeappx pack failed (the manifest did not validate - see the output above)' }
if ($Check) {
    $mb = [math]::Round((Get-Item $pkg).Length / 1MB, 1)
    Remove-Item $pkg
    Write-Host "MSIX check passed: the manifest validates and $version packs ($mb MB). Nothing was written to dist." -ForegroundColor Green
    return
}

function Write-Sums {
    $sums = Get-ChildItem $out -File -Recurse | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.FullName.Substring((Resolve-Path $out).Path.Length + 1).Replace('\', '/')
    }
    [IO.File]::WriteAllLines((Join-Path (Resolve-Path $out).Path 'SHA256SUMS.txt'), $sums)
}

if ($TestSign) {
    # Local test only: a self-signed certificate in YOUR certificate store, subject = the test Publisher.
    $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object Subject -eq $identity.Publisher | Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -Type Custom -Subject $identity.Publisher -KeyUsage DigitalSignature -FriendlyName 'DarkCipher MaskedMail local MSIX test' `
            -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(2) -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    }
    & $signtool.FullName sign /fd SHA256 /sha1 $cert.Thumbprint /q $pkg
    if ($LASTEXITCODE) { throw 'signing the test package failed' }
    $cer = Join-Path $out "DarkCipherMaskedMail-$version-local-test.cer"
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    Write-Host "Test package: $pkg" -ForegroundColor Green
    Write-Host 'To install it on this PC, trust the test certificate once from an ELEVATED PowerShell, then double-click the .msix:' -ForegroundColor Yellow
    Write-Host "  Import-Certificate -FilePath '$((Resolve-Path $cer).Path)' -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
    Write-Host 'Never publish the -test.msix or the .cer: they are for this PC only. Remove the trust afterwards (certlm.msc -> Trusted People).'
    return
}

Write-Sums
Write-Host "Store package (unsigned - the Store signs it): $pkg" -ForegroundColor Green
Write-Host 'Upload it in Partner Center -> your app -> Packages. See installer\msix\STORE-SUBMISSION.md.'
