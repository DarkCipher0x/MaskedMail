# Contributing

Thanks for looking. A few rules keep MaskedMail small, readable and safe.

## Build and test

You need Windows 10/11 and the .NET 8 SDK.

```bash
dotnet build DarkCipher.MaskedMail.sln -c Release
dotnet test DarkCipher.MaskedMail.sln -c Release
```

`build.ps1` does the full release build: locked restore, build (every warning is an error, all .NET security analyzers
on, NuGet vulnerability audit), unit tests, the exe self-test, the MSI and the privacy-scanned source. See
[docs/TESTING.md](docs/TESTING.md) and [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## House rules

- **Security first.** Keys and tokens only ever live in the encrypted settings. All network traffic goes through
  `Net` (HTTPS, TLS 1.2+, no redirects). Nothing from a mailbox is written to disk.
- **Small projects, one job each.** References only point down the list in ARCHITECTURE.md.
- **A new service is one file** in `src/DcxMail.Providers.Masked`, `.Domains` or `.Disposable`, plus one line in
  `ServiceCatalog`. It describes itself with a `ServiceInfo` and only declares features it implements (a unit test
  checks). Services whose inboxes anyone can read, or that have no public, documented API, are left out on purpose.
- **Tests use the fakes** in `src/DcxMail.Fakes`, never a real account or the network.
- **No real data anywhere:** use `example.com` addresses and made-up keys in code, tests and screenshots.
- **The self-test never shows a window** (renders are off-screen, dialogs refuse).

## Security problems

Please don't open an issue. Use the repository's **Security** tab → **Report a vulnerability** (see
[.github/SECURITY.md](.github/SECURITY.md)).
