# DarkCipher MaskedMail

A small Windows app for masked email. It creates and manages masked and throwaway addresses, gives each one its own
folder, files incoming mail into that folder, and lets you write HTML email from any of them. Everything it stores is
encrypted. It uses the same look and security model as DarkCipher ServerManagement.

## Documentation

| Document | For |
|---|---|
| [User guide](docs/USER-GUIDE.md) | Everything the app does, step by step, plus troubleshooting |
| [Security model](docs/SECURITY.md) | What is protected, how, and the limits |
| [Security audit 0.1.6](docs/SECURITY-AUDIT-0.1.6.md) | The hardening review: findings, fixes and how they're verified |
| [Privacy policy](docs/PRIVACY.md) | What the app stores and sends (also the Store listing's policy) |
| [Add-ons](docs/ADDONS.md) | Writing and packing add-ons (API 2, encrypted bridge) |
| [Architecture](docs/ARCHITECTURE.md) | How the code fits together, key decisions, release process |
| [Testing](docs/TESTING.md) | The unit tests (128) and the exe self-test, and what the build gates on |
| [Changelog](docs/CHANGELOG.md) | Every version, newest first (generated from [release notes](docs/release-notes/)) |
| [Microsoft Store submission](installer/msix/STORE-SUBMISSION.md) | MSIX packaging, listing text, certification answers |
| [Contributing](CONTRIBUTING.md) | Building, testing and the house rules for changes |
| [Security policy](.github/SECURITY.md) | How to report a vulnerability privately |
| [Third-party notices](THIRD-PARTY-NOTICES.md) | Components and their licences |

The documentation also ships inside every release (the zip's `docs\` folder).

## Services

Thirteen services, in three kinds. Each does everything its API allows; the app only offers what a service supports.

| Kind | Services | Mail is read… | Send from the address |
|---|---|---|---|
| **Masked email** | **Fastmail** Masked Email (JMAP), **SimpleLogin** (and Proton Pass aliases), **addy.io**, **Firefox Relay**, **DuckDuckGo** (@duck.com) | in its own Fastmail folder, when it forwards to your Fastmail account | Fastmail directly; SimpleLogin and addy.io through reverse aliases; Relay and DuckDuckGo can't |
| **Your own domains** | **Forward Email**, **Cloudflare Email Routing**, **ImprovMX**, **Migadu**, **mailcow** (self-hosted, with server-side time-limited aliases) | in its Fastmail folder, when it forwards to Fastmail | Forward Email through its own outbound mail |
| **Disposable inboxes** (off until you turn them on) | **Mail.tm / Mail.gw**, **TempMail.lol**, **DropMail** | inside the app, straight from the service | — |

Fastmail is the mail account: the app reads, files and sends mail through it. The others are optional.
SimpleLogin, addy.io and mailcow can point at self-hosted servers. The [user guide](docs/USER-GUIDE.md#services) lists
every feature per service, and the services left out (public inboxes anyone can read, services without an API) with
the reason.

## Install

Download the latest release from the [Releases page](https://github.com/DarkCipher0x/MaskedMail/releases/latest). Each
release has the MSI, a portable zip, `SHA256SUMS.txt`, the test reports and the source it was built from. Windows 10 or
11 (x64) is required.

**Website edition (MSI).**
- Run `DarkCipherMaskedMail-<version>-x64.msi`. It installs to
  `C:\Program Files\DarkCipher Systems\MaskedMail`, adds Start menu shortcuts and an optional desktop shortcut, and
  offers the WebView2 Runtime if it's missing.
- Later versions upgrade in place. Uninstalling asks whether to delete your encrypted settings and cache; nothing is
  deleted unless you tick it.
- The zip has the same exe for portable use.
- The MSI isn't code-signed, so SmartScreen warns. Check it against `SHA256SUMS.txt`.

**Microsoft Store edition (MSIX)** is prepared but not yet submitted.
- **Binaries:** the same as the MSI. The Store signs the package, so there's no SmartScreen warning.
- **Data:** it shares the same encrypted data folder as the MSI.
- **Start with Windows** is a Windows startup task.

See [STORE-SUBMISSION.md](installer/msix/STORE-SUBMISSION.md).

## Set-up

1. In Fastmail, go to **Settings → Privacy & Security → Manage API tokens** and create a token with:
   - **Masked Email**
   - **Email** (read/write)
   - **Email submission**
2. Start MaskedMail. Open **⚙ Settings → Fastmail**, paste the token, and press **Test connection**.
3. Optional: set up other services under **Settings → Masked-email services**, **Your own domains** or **Disposable
   inboxes**.
4. Optional: **Settings → Folders & routing → Create folders for every existing address**.
5. Recommended: **Settings → Security & privacy**. Make an encrypted backup, then set a master password and 2FA.

## Throwaway addresses

Use **⏱ Throwaway ▾** (or Ctrl+Shift+T) to make a random address that ends itself after 10 minutes, 1 hour, 1 day, 7
days, 30 days or a lifetime you choose.
- The address is copied to the clipboard and a **live inbox** opens, which picks out verification codes and
  confirmation links.
- On expiry the address is deleted (or disabled) and its mail goes to Trash.
- You can extend it, keep it as a normal address, or burn it early.

## Security

The security options match DarkCipher ServerManagement's. They're in **Settings → Security & privacy**, or click the
🔒 badge in the status bar.

- **Encrypted storage.** Every API key, the settings, the folder map, the outgoing queue and the security log are
  encrypted with AES-256-GCM under one random data key.
  - The key is protected by Windows DPAPI.
  - It can also be protected by a **master password** (PBKDF2-SHA256, 600k), which can be required at every start.
- **Two-factor sign-in.** Works with any TOTP authenticator app, with 10 single-use recovery codes.
- **App lock.** After inactivity, when Windows locks, or with 🔒 / Ctrl+Shift+L. Mail keeps being filed while locked.
- **Sandbox mode.** The password is required at every start, auto-lock is on, and the viewer cache is wiped on exit.
- **Encrypted backup / restore** (`.dcxbackup`), sealed with its own password.
- **Security log.** Every entry is encrypted and hash-chained, so removed lines are flagged.
- **Screen capture.** The sign-in screen, 2FA set-up, recovery codes and password prompts never show in screenshots or
  streams. There's an option to hide every window.
- **Network.** HTTPS (TLS 1.2+) only, and redirects are never followed. No telemetry, no account of its own.
- **Mail.** Scripts never run, remote images are blocked until you load them, and message content is never written to
  disk.

## Outgoing queue

Press **📤 Queue** or Ctrl+Shift+Q.
- **From the composer:** use **Add to queue**. Set **Copies** to send several identical copies, or tick **separate copy
  per recipient** so each person gets their own email.
- **In the queue:** edit, duplicate, set copies, split into one copy per recipient, change the sender, reorder,
  skip/include, or remove each item.
- **Sending:** **Send all** sends with a pause between emails. Failed items stay queued with their error.
- **Storage:** the queue is encrypted on disk.

## Add-ons

Open **◇ Add-ons** in the header, or Settings → Add-ons. The model is the same as ServerManagement's:
- **Sandbox:** add-ons run at Low integrity in a Job.
- **Encrypted bridge:** an ECDH key exchange, then AES-256-GCM.
- **Approval:** you approve each permission, and every change or send an add-on asks for is shown to you first.
- **Integrity:** files are hash-checked before every run.
- **Themes:** theme add-ons are encrypted on disk.

The SDK and samples are in `samples/addons`, and the guide is [ADDONS.md](docs/ADDONS.md). Packed samples are in
`dist\addons\`.

## Folders and routing

Every masked address can have the folder **Inbox/automation/&lt;address&gt;**. You can change the parent folder in
Settings.

Fastmail's API can't create mail rules, so MaskedMail does the filing itself:
- It checks the Inbox every 2 minutes (you can change this).
- It moves mail addressed to a routed address into that address's folder. It checks To, Cc, Bcc and Delivered-To.
- It runs while the window is open or the app is in the notification area.
- **Start with Windows** keeps it going after a reboot.

## Writing and reading mail

**The composer** is a lightweight HTML editor:
- fonts, sizes, bold, italic, underline and strikethrough;
- colours, lists, alignment and quotes;
- links;
- images (insert, paste or drag in) and attachments;
- copies, and **Add to queue**.

Images go out as inline (cid) parts, and a plain-text alternative is added automatically.

**The reader** blocks scripts and remote content until you press **Load images**. Links show their real address before
they open.

## Building

```bash
powershell -ExecutionPolicy Bypass -File build.ps1
```

`build.ps1` does the following:
1. Restores with locked package versions and builds the whole solution. Every warning is an error: .NET analyzers
   (all security rules) and a NuGet vulnerability audit.
2. Runs the unit tests (`dotnet test`, 128 tests against in-process fakes, including real sandboxed add-on runs).
3. Publishes a self-contained single-file exe.
4. Runs `DcxMaskedMail.exe --selftest <report>`: the UI and runtime checks, with every window drawn off-screen. It
   stops on any failure.
5. Builds the MSI (WiX 5, pinned in `.config\dotnet-tools.json`).
6. Writes `dist\<version>\`: the MSI, zip, exe, `docs\` (including the generated changelog), `store\` (submission
   guide, privacy policy and screenshots), the test and self-test reports and `SHA256SUMS.txt`.
7. Builds the MSIX with `tools\make-msix.ps1`. Until the Store identity is filled in, it validates and packs the MSIX
   without keeping it (`-Check`).

Bump `<Version>` in `Directory.Build.props` and add `docs\release-notes\<version>.md` before each build; the build
refuses otherwise. Old releases are kept. The MSI's UpgradeCode `053f918a-404b-4528-b56e-ffc0c1e76023` must never change.

The code is split into small projects (`DarkCipher.MaskedMail.sln`):
- Core (storage and security);
- Mail (JMAP and HTML);
- Providers (what every service shares) and one project per kind of service (Masked, Domains, Disposable), with one
  file per service;
- Aliases (Fastmail, the service catalog, throwaways, queue);
- Addons (sandbox and bridge);
- Fakes (test servers);
- the WPF app, and the tests.

See [Architecture](docs/ARCHITECTURE.md).

MIT licence, © 2026 darkcipher0x.
