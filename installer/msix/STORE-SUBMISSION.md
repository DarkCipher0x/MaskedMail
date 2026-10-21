# DarkCipher MaskedMail — Microsoft Store submission

The Microsoft Store signs MSIX packages for free after certification. That is the only free route to a signed app
with no SmartScreen warnings, and it's the same approach as DarkCipher ServerManagement. The website MSI and zip stay
unsigned.

**Status:** everything is prepared and validated (`make-msix.ps1 -Check` passes), but **nothing has been submitted**.
The Partner Center steps below are yours to do.

## What is in the repo

| File | Purpose |
|---|---|
| `installer\msix\AppxManifest.xml` | MSIX manifest: a full-trust desktop app. File and registry write virtualization are off, so a Store install uses the same `%APPDATA%\DarkCipher\MaskedMail` store as the MSI. It also declares the **startup task** ("Start with Windows"). |
| `installer\msix\store-identity.json` | The three identity values from Partner Center. Empty until you reserve the name. |
| `tools\make-msix.ps1` | Packages `.\publish` (built by `build.ps1`) as `dist\<ver>\DarkCipherMaskedMail-<ver>-x64.msix`, and draws the tile images from the app logo. |
| `tools\msix\MsixTools.csproj` | Pins Microsoft's `makeappx` and `signtool` (`Microsoft.Windows.SDK.BuildTools` 10.0.28000.1839). |
| `src\DcxMaskedMail\Services\Packaging.cs` | Detects a Store install. When it finds one, it uses the manifest's taskbar identity and the Windows startup task, and starts in the tray when Windows launches it at sign-in. |
| `dist\<ver>\store\` | Written by every build: these instructions, the privacy policy and Store-size screenshots (demo data only). |

## One-time setup (you)

1. **Register as a developer.** Go to <https://storedeveloper.microsoft.com> and register as an **Individual**. It's
   free, and the identity check can take a few days. If you already registered for ServerManagement, WhisperDesk or
   CaseKeeper, use that account.
2. **Reserve the name.** In [Partner Center](https://partner.microsoft.com/dashboard), go to Apps and games → New
   product → **MSIX or PWA app**, and reserve **DarkCipher MaskedMail**.
   - Don't pick "EXE or MSI app": that route makes us sign the installer ourselves.
3. **Copy the identity.** Open the product → Product management → **Product identity**. Copy these into
   `installer\msix\store-identity.json`:
   - `Package/Identity/Name` → `IdentityName`
   - `Package/Identity/Publisher` → `Publisher` (the whole `CN=…` value)
   - `Package/Properties/PublisherDisplayName` → `PublisherDisplayName`
4. **Publish the privacy policy.** Put `docs\PRIVACY.md` on the website (for example
   `https://darkciphersystems.com/maskedmail/privacy`) and use that URL in the listing.

## Each release

```bash
powershell -ExecutionPolicy Bypass -File build.ps1
```

`build.ps1` builds the MSI and zip as before.
- **Before the identity is filled in**, it runs `make-msix.ps1 -Check`. That validates the manifest and packs to a temp
  file, so a broken MSIX never goes unnoticed.
- **Once the identity is filled in**, it writes the Store package `dist\<ver>\DarkCipherMaskedMail-<ver>-x64.msix` and
  adds it to `SHA256SUMS.txt`.

The MSIX version is the app version with `.0` added (0.1.8 becomes 0.1.8.0). Every build bumps the version, so each
submission is higher than the last.

Upload the `.msix` under the submission's **Packages**. Don't sign it: the Store does that.

## Test on this PC before submitting (optional)

```bash
powershell -ExecutionPolicy Bypass -File tools\make-msix.ps1 -TestSign
```

1. The command makes `…-x64-test.msix` and `…-local-test.cer`. They use a local test identity and a self-signed
   certificate, created in your own `CurrentUser\My` store.
2. Trust that certificate once, from an **elevated** PowerShell. The script prints the exact `Import-Certificate` line.
3. Double-click the `.msix` to install it.
4. Check these:
   - It starts, and your settings are there, shared with the MSI install.
   - **Start with Windows** turns on (Settings → Appearance & behaviour) and shows in Task Manager → Startup apps.
   - A throwaway's live inbox and an add-on run work.
5. Uninstall it, then remove the certificate in certlm.msc → Trusted People.
6. Never upload the test files.

## Store listing (copy and paste)

**Product name:** DarkCipher MaskedMail

**Short description** (about 100 characters):

> Masked and throwaway email addresses with an encrypted mail client for Fastmail and other alias services.

**Description:**

> Keep your real email address private. DarkCipher MaskedMail creates a different masked address for every site, files
> each one's mail into its own folder, and lets you write and send HTML email from any of them, all from one small,
> encrypted desktop app.
>
> • Masked addresses from Fastmail, SimpleLogin (and Proton Pass), addy.io, Firefox Relay and DuckDuckGo, side by side
> • Addresses on your own domains with Forward Email, Cloudflare Email Routing, ImprovMX, Migadu or your mailcow server
> • Optional disposable inboxes (Mail.tm, TempMail.lol, DropMail), read right in the app
> • Throwaway addresses that end themselves after 10 minutes, an hour, a day or a week, with a live inbox that picks
>   out verification codes and confirmation links
> • Each address gets its own folder; incoming mail is filed automatically
> • A lightweight HTML composer: fonts, colours, lists, links, inline images and attachments
> • An outgoing queue: collect emails (or copies of one), then send them together
> • Everything stored is encrypted (AES-256-GCM), with an optional master password, two-factor sign-in, app lock and
>   encrypted backups
> • Remote images and tracking pixels are blocked until you choose to load them; scripts in mail never run
> • Sandboxed add-ons over an encrypted bridge, and colour themes
> • No account of its own, no telemetry: it talks only to the services you set up
>
> Needs a Fastmail account (API token) for mail, folders and sending. Every other service is optional.

**Features** (up to 20 short lines):
- Masked email from Fastmail, SimpleLogin, addy.io, Firefox Relay and DuckDuckGo
- Addresses on your own domains (Forward Email, Cloudflare, ImprovMX, Migadu, mailcow)
- Optional disposable inboxes read in the app
- Throwaway addresses that expire on their own
- Live inbox with verification codes and links picked out
- A folder per address, with mail filed automatically
- HTML email with fonts, images and attachments
- An outgoing queue with copies and per-recipient sends
- AES-256-GCM encryption of everything stored
- Master password and two-factor sign-in
- App lock, sandbox mode and encrypted backups
- Remote images and tracking pixels blocked
- Sandboxed add-ons and colour themes
- Dark and light themes
- No telemetry, no account of its own

**Keywords** (up to 7): masked email, email alias, throwaway email, disposable email, Fastmail, privacy, SimpleLogin

**Category:** Productivity (alternative: Security).

**Pricing:** free.

**Privacy policy URL:** where you published `docs\PRIVACY.md`. It's required: the app handles email and makes network
connections.

**Website:** `https://darkciphersystems.com`

**Support contact:** your public support address. Don't use a personal one.

**Screenshots:**
- Use `dist\<ver>\store\screenshots\*.png`. They are 1584 × 921, above the Store minimum of 1366 × 768, and show
  demo data only (example.com addresses, "Alex").
- Look at every image before uploading.
- The reading pane is blank in these off-screen renders, so screenshots taken from the real app with a demo account
  look better.

**Age rating (IARC questionnaire):**
- Users don't share user-generated content with each other in the app.
- There's no chat, no location sharing and no purchases.
- It does send and receive email, which is user communication. Answer the communication question yes; the rating is
  normally 3+ or 12+.

## Restricted capabilities: justification for certification

- **runFullTrust:** "Win32 desktop application (WPF, .NET 8). It runs a notification-area process that files mail on
  a timer, hosts WebView2 for the message viewer and editor, and runs optional user-installed add-ons in a Windows
  Low-integrity sandbox (CreateProcessAsUser and Job objects). These need a full-trust process."
- **unvirtualizedResources:** "The app keeps an encrypted settings store under %APPDATA%\DarkCipher\MaskedMail. It is
  shared with the same product's MSI edition, so users can move between editions without losing data, and an uninstall
  never silently deletes the user's encrypted keys. The app writes only under its own DarkCipher folders."

**Notes for certification:** "No account is needed to start the app. It manages masked email addresses for services
the user already has (Fastmail is required for mail; twelve other alias, domain and disposable-inbox services are
optional, and the disposable ones are off until the user turns them on). Without an
API token the app opens to its Settings and can be explored; mail features need the user's own Fastmail API token."

## Known differences in a Store install

- **No data clean-up on uninstall.** The MSI's "also delete my data" option (`--cleanup`) doesn't exist in MSIX. The
  encrypted data folder stays, which is deliberate. To remove it, delete `%APPDATA%\DarkCipher\MaskedMail` after
  uninstalling. Make an encrypted backup first if you might want it back.
- **"Start with Windows" is a Windows startup task.** You can turn it off from Task Manager → Startup apps. If you
  turned it off there, the app can't turn it back on and says so.
- **Windows 10 1903 or later.** The MSI supports 1809 or later; the MSIX needs 1903 because the virtualization
  opt-out requires it.
- **Fallback if certification refuses `unvirtualizedResources`.** Remove the two `desktop6:` lines and that
  capability. The store then lives in the package's private AppData, separate from the MSI install, and **is deleted
  on uninstall**. Tell users to make an encrypted backup first.
- **Add-ons in a Store install haven't been tested yet.** The add-on sandbox (Low integrity plus a Job) should work
  from a full-trust package, but check it with the `-TestSign` build first.
