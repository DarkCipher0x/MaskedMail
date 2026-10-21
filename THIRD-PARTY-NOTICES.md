# Third-party notices

DarkCipher MaskedMail is MIT-licensed (see `LICENSE`). It ships with or builds on the following components, under their
own licences. Versions are pinned in `Directory.Packages.props` and the `packages.lock.json` files.

## Shipped in the app

| Component | Version | Licence | Used for |
|---|---|---|---|
| [.NET 8 runtime and WPF](https://github.com/dotnet/runtime) | 8.0 | MIT | The app runs on it (published self-contained) |
| [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2) | 1.0.2903.40 | Microsoft BSD-style licence (see the package) | The message viewer and the HTML editor |
| [System.Security.Cryptography.ProtectedData](https://www.nuget.org/packages/System.Security.Cryptography.ProtectedData) | 8.0.0 | MIT | Windows DPAPI protection of the data key |
| [QRCoder](https://github.com/codebude/QRCoder) | 1.6.0 | MIT | The 2FA set-up QR code |

The Microsoft Edge WebView2 Runtime is installed separately by Windows (the MSI offers it if it's missing) and is
covered by Microsoft's own terms.

## Used only to build and test

| Component | Licence |
|---|---|
| [WiX Toolset 5](https://github.com/wixtoolset/wix) (`.config/dotnet-tools.json`) | MS-RL |
| [xUnit](https://github.com/xunit/xunit), [Microsoft.NET.Test.Sdk](https://github.com/microsoft/vstest) | Apache-2.0, MIT |

## Services

The app talks to Fastmail and, if you set them up, the other services listed in the README. They are independent
services with their own terms and privacy policies; MaskedMail isn't affiliated with or endorsed by any of them. All
product names are trademarks of their owners.
