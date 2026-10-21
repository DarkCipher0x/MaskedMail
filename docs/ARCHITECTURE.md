# DarkCipher MaskedMail — architecture

The app is WPF on .NET 8, published as a self-contained single-file exe (`DcxMaskedMail.exe`, namespace `DcxMail`).
It uses WebView2 for the message viewer and the HTML editor, and QRCoder for the 2FA QR code. Nothing else is
third-party. The look (palette, controls, logo ring) is shared with DarkCipher ServerManagement.

## Solution layout (`DarkCipher.MaskedMail.sln`)

Ten projects plus the tests, each with one job. References only point down the list, so the libraries never see WPF
and every rule can be tested without a window.

| Project | References | Role |
|---|---|---|
| `src/DcxMail.Core` | (none) | Storage and security: `SecureStore` (key ring: DPAPI / master password, TOTP, throttling; AES-256-GCM files), `AppSettings`, `AuditLog` (hash-chained), `ErrorLog`, `Backup`, `Totp`, `FolderAcl`, `Net` (the only HTTP client factory: HTTPS only, no redirects, TLS 1.2+), `ProcessHardening`, `SecurityPolicy`, `Packaging`, `AppInfo`; the add-on and throwaway models |
| `src/DcxMail.Mail` | Core | Fastmail JMAP (`JmapClient`: session, batched calls, blobs), `MailService` (folders, routing sweep, read, send with a MIME tree and inline cid images), `HtmlTools` (sanitizer, plain text, reader page), `Addresses` |
| `src/DcxMail.Providers` | Mail | What every service shares: `Alias`, `CreateRequest`, `SendMode`; `ServiceInfo` (each service's own description: kind, features, settings fields, caution, set-up help); `IAliasProvider` and the optional abilities `IDomainChoice`, `IForwardChoice`, `IInboxProvider`, `ISendingProvider`, `IActivityProvider`; `RestProvider` (HTTPS through `Net`, same-host links only, size cap, plain-sentence errors); `LocalAliases` (addresses the app remembers, encrypted); `ProviderInbox`; `RandomName` |
| `src/DcxMail.Providers.Masked` | Providers | One file per masked-email service: SimpleLogin, addy.io, Firefox Relay, DuckDuckGo |
| `src/DcxMail.Providers.Domains` | Providers | One file per own-domain service: Forward Email, Cloudflare Email Routing, ImprovMX, Migadu, mailcow |
| `src/DcxMail.Providers.Disposable` | Providers | One file per disposable-inbox service: Mail.tm (Mail.gw), TempMail.lol, DropMail; `InboxState` for services without read flags |
| `src/DcxMail.Aliases` | Mail, Providers.* | Fastmail Masked Email; `ServiceCatalog` (every service in order, and those left out with the reason); `ProviderRegistry` builds the configured ones, owns routing and says where an address's mail is read (`MailFor`); `Throwaways` (create, extend, keep, burn in the strongest way the service has, enforce expiry; codes and links); `Outbox` (the encrypted queue) |
| `src/DcxMail.Addons` | Aliases | Packages (inspect, install, seal, verify), `SandboxProcess` (Low IL + Job + locked DACL), `AddonBridge` (ECDH → HKDF → AES-GCM), `AddonHost` (run loop and methods), theme add-ons, Authenticode. UI needs (approval, file picker, status, dispatcher) are seams the app fills in; their defaults refuse |
| `src/DcxMail.Fakes` | Mail | In-process fake servers: Fastmail JMAP and a REST fake (with a request log) for every other service. Used by the tests and the self-test renders; never the network |
| `src/DcxMaskedMail` | all of the above | The WPF app: start-up (`App.xaml.cs`: process hardening → single instance → sign-in → settings → main window; crash capture to `ErrorLog`), main window, views (composer with WebView2 + `Assets/web/editor.html`, queue, settings, new address, throwaway inbox, add-ons, sign-in, dialogs), `ThemeManager`, `AppLock`, `CaptureGuard`, `ClipboardHelper`, and `SelfTest.cs` (UI and runtime checks plus the renders) |
| `tests/DcxMail.Tests` | libraries + Fakes | xUnit: store and security, HTML, every provider and the queue, throwaways, add-ons with the real sandbox (see `docs/TESTING.md`) |

Shared build rules are in the repository root:
- **`Directory.Build.props`** holds the one version number, .NET 8 / win-x64, nullable, and the quality gates: every
  warning is an error, .NET analyzers at `latest` with **all security rules** and the recommended reliability rules,
  package lock files, a NuGet vulnerability audit of every package (direct and transitive), path maps, and no symbols
  in Release builds.
- **`Directory.Packages.props`** pins every NuGet version (central package management).
- **`Directory.Build.targets`** removes the WinForms implicit usings.
- **`src/Shared/DllSearchPaths.cs`** is linked into every project, so native DLLs only ever load from System32.

## Key decisions

1. **Fastmail is the mailbox; the other services are alias sources.** Masked-email and own-domain addresses forward to
   your real mailbox. When that mailbox is the Fastmail account, their mail is filed and sent through Fastmail (reverse
   aliases for SimpleLogin and addy.io). Disposable inboxes are the exception: their mail stays at the service and is
   read there, through `IMailSource` (a Fastmail folder and a provider inbox look the same to every window).
2. **A service describes itself; the app follows the description.** Each service file declares a `ServiceInfo`:
   - its kind and features (`Features` flags);
   - its settings fields (Settings builds the card from them: `ServiceSettingsCard`);
   - its caution, opt-in tick and set-up help.

   The app offers only declared features, on the card, in the new-address form and in the add-on API. A unit test
   checks each declaration against the interfaces the class really implements. Adding a service means one file plus
   one line in `ServiceCatalog`.
3. **Throwaways end in the strongest way each service has:** delete (or turn off, by your choice), a server-side
   time limit where one exists (mailcow), the service's own lifetime for disposable inboxes (capped, not extendable),
   and an honest "removed from the list" where the service can't delete at all (DuckDuckGo). The security log
   records which.
4. **Routing is done by the app.** JMAP doesn't expose Fastmail's rules, so the app sweeps the newest Inbox mail on a
   timer and moves it. The sweep runs in the tray and while the app is locked, and a "start with Windows" option keeps
   it going.
5. **Nothing from your mailbox is stored.** Messages are fetched on demand, and big ones are served to the viewer from
   memory. Only your own drafts in the queue are kept, and they're encrypted.
6. **Defence in depth for HTML:**
   - a server-side sanitizer;
   - the viewer with scripts off and `default-src 'none'`;
   - the editor with a per-load script nonce and an inert DOM sanitizer;
   - WebView2 with permissions, downloads and navigation locked down.
7. **Security parity with ServerManagement:**
   - the same key-ring design, TOTP, app lock, sandbox mode and backup format idea;
   - the same add-on sandbox, bridge protocol (the SDK DLL is identical) and package rules.
8. **Tests gate the build.** `build.ps1` refuses to package if the solution has any warning (analyzers, NuGet audit),
   if a unit test fails, or if the exe self-test fails. Self-test windows are always off-screen, and dialogs refuse
   while it runs.
9. **Two distribution channels, one set of binaries:**
   - **The website MSI and zip.** These are unsigned.
   - **A Microsoft Store MSIX.** This is free signing, as in ServerManagement's ADR-029.
     - It packs the very same `publish` folder.
     - Write virtualization is off (`unvirtualizedResources`), so both editions share
       `%APPDATA%\DarkCipher\MaskedMail`, and uninstalling the Store edition never deletes the encrypted store.
     - "Start with Windows" is a manifest startup task when packaged, because a Run key can't start a packaged app.
       Otherwise it is the HKCU Run key.
10. **Throwaways are ordinary masked addresses plus a record.** The address, its service and its expiry are kept in the
   encrypted settings. Expiry is enforced by the app (every 30 s and at start), never by a provider feature, so it
   behaves the same on every service.

## Data flow: creating a masked address

```
NewAliasWindow → ProviderRegistry.CreateAsync(provider, request)
  → IAliasProvider.CreateAsync           (Fastmail: MaskedEmail/set state=enabled, forDomain, emailPrefix)
  → EnsureFolderAsync                     (Mailbox/get + Mailbox/set: Inbox → automation → <address>)
  → AppSettings.Folders[address] = id     (encrypted)
  → SweepAsync                            (Email/query inbox + Email/get to/cc/bcc/Delivered-To; only if something
                                           matches: Mailbox/get, then Email/set mailboxIds into folders that still exist)
  → AuditLog "masked address created"
```

## Data flow: sending

```
ComposeWindow.CollectAsync → QueuedEmail(s)    (one per recipient if asked; copies)
  → Send now: Outbox.SendItemAsync   |   Queue: Outbox.Put → outbox.dcx (encrypted) → OutboxWindow → SendAllAsync
  → Outbox.BuildAsync                          (sender: masked/identity/reverse alias)
  → MailService.SendAsync                      (inline data: images → blobs/cid, attachments → blobs,
                                                Email/set draft + EmailSubmission/set → Sent)
```

## Release process

1. Bump `<Version>` in `Directory.Build.props`, and add `docs/release-notes/<version>.md`. The build refuses without it.
2. Run `build.ps1`. It:
   - restores with locked package versions and builds the whole solution (warnings are errors);
   - runs the unit tests;
   - publishes;
   - runs the exe self-test;
   - generates `docs/CHANGELOG.md`;
   - builds the MSI (WiX 5);
   - writes `dist\<version>\`: the MSI, zip, exe, `docs\`, `store\` (submission guide, privacy policy and Store-size
     screenshots, checked to be at least 1366×768), release notes, the unit-test and self-test reports, the
     sanitised source zip (allowlist, privacy scan, gitleaks) and `SHA256SUMS.txt`;
   - runs `tools\make-msix.ps1`. That writes the Store package once `installer\msix\store-identity.json` is filled in,
     and otherwise `-Check` (validate and pack, keep nothing).

   Old releases are kept.
3. **Publishing:** `release.ps1` checks the build, rebuilds the public copy from the source zip and pushes it to the
   public repository, then creates the GitHub Release. It has a dry run, a kill switch (`DCX_PUBLISH_OFF`), and a
   record of every step, so it can be safely run again. See [RELEASING.md](RELEASING.md).
4. **Microsoft Store:** follow `installer\msix\STORE-SUBMISSION.md`. `make-msix.ps1 -TestSign` makes a local,
   self-signed test package.
5. **Add-ons:** `samples\addons\pack.ps1 <folder>` writes to `dist\addons\<id>\`.
6. **Identities that must never change:**
   - the MSI's UpgradeCode `053f918a-404b-4528-b56e-ffc0c1e76023`;
   - the MSIX identity, once it's reserved in Partner Center.
