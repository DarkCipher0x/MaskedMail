# DarkCipher MaskedMail — security model

This document describes what MaskedMail protects, how it does it, and what it can't protect. The latest audit is in
[SECURITY-AUDIT-0.1.6.md](SECURITY-AUDIT-0.1.6.md).

## What is stored, and where

All of the following are under `%APPDATA%\DarkCipher\MaskedMail`, in a folder only your Windows account and SYSTEM can
open:

| File | Holds | Protection |
|---|---|---|
| `keyring.json` | The data key, wrapped (never in the clear); the 2FA secret, sealed; recovery-code hashes; the sign-in failure count | DPAPI and/or the master password |
| `settings.dcx` | Every API key and token, the folder map, throwaway addresses and their expiry times, installed add-ons (manifests, grants, file hashes), all preferences | AES-256-GCM |
| `outbox.dcx` | The outgoing queue (queued emails and their attachments) | AES-256-GCM |
| `audit.log` | The security log, one sealed line per event, hash-chained | AES-256-GCM per line |
| `errors.log` | Unexpected errors (type, message, stack trace without local paths), newest 1000 | AES-256-GCM per line |

These are under `%LOCALAPPDATA%\DarkCipher\MaskedMail`, which is also locked to you:

| Folder | Holds |
|---|---|
| `WebView2\` | The message viewer and editor's browser profile. No mail is written here by the app; it is wiped on exit in sandbox mode. |
| `addons\<id>\<version>\` | Installed add-on files. Themes are AES-256-GCM-sealed. Program files stay readable so Windows can run them, and are hash-checked before every run. |

`%USERPROFILE%\AppData\LocalLow\DarkCipher\MaskedMail\addons\<id>` is each add-on's only writable folder.

**Your mail is not stored on this PC.** Messages are read from Fastmail when you open them. Large messages are shown
from memory, never from a temporary file.

## Encryption

- **One random 256-bit data key.** Each file is encrypted with AES-256-GCM using a fresh 96-bit nonce. The file's name
  is the associated data, so encrypted files can't be swapped or replayed under another name. Any change to a file is
  detected when it is read.
- **Key wrapping:**
  - **Windows DPAPI** (current user): the app opens when you sign in to Windows.
  - **Master password** (PBKDF2-SHA256, 600,000 iterations, random 128-bit salt): optional. With **Require it at
    start** ticked, the DPAPI copy is deleted, so the files can't be decrypted without the password, even by an
    administrator or from a copy of the disk.
- **Backups** (`.dcxbackup`) are sealed with a password of their own, so they can be restored on another PC.
- **Security log:** every line is sealed on its own and holds a hash of the line before it. Removed or reordered
  entries show as "⚠ log altered".

## Signing in and locking

- **Master password:**
  - at least 12 characters, at least 4 different ones;
  - not a common password with something added;
  - mixed kinds of character, unless it is a passphrase of 16+ characters.
- **Two-factor sign-in (TOTP, RFC 6238):**
  - works with any authenticator app; the secret is sealed with the data key;
  - a code can't be used twice, and ±30 s of clock drift is allowed;
  - 10 single-use recovery codes, stored as salted hashes.
- **Wrong attempts:** counted across restarts. From the 4th in a row, each try waits longer (up to 5 minutes). A
  correct sign-in resets the count. Every attempt is logged.
- **App lock:**
  - on inactivity (5 min – 4 h), when Windows locks or the PC sleeps, or with 🔒 / Ctrl+Shift+L;
  - while locked, every window is hidden and mail keeps being filed;
  - from the tray, the lock takes effect at the next open.
- **Sandbox mode:** the master password is required at every start, auto-lock is always on, and the viewer cache is
  wiped on exit.
- **Screen capture:** the sign-in screen, 2FA set-up, recovery codes and password prompts never appear in screenshots,
  recordings or streams. You can choose to hide every window (Settings → Security & privacy).

## Throwaway addresses

- **On expiry:** the address is deleted (or only disabled, if you choose that), so mail sent to it afterwards bounces
  (or is dropped). Its mail is **moved to Trash**, never destroyed by the app, and its folder is removed.
- **When expiry is checked:** every 30 s while the app runs, and at every start.
- **If ending fails:** a throwaway that couldn't be ended (for example, the service was unreachable) is kept with the
  reason and retried. It is never silently forgotten.
- **Logging:** every create, extend, keep and end is in the security log.
- **The live inbox** shows plain text only. Codes and links are found locally. A link opens in your browser only after
  you confirm its real address, as everywhere else.

## Network

- Every service (Fastmail's JMAP and the twelve others below) is reached only over **HTTPS with TLS 1.2/1.3**, with
  Windows' certificate validation, through the app's one hardened HTTP client. A self-hosted server (mailcow,
  SimpleLogin, addy.io) given as `http://` is refused before anything is sent.
- **Redirects are never followed**, so a key can't be carried to another host.
- **A service can't send the app elsewhere.** A link a service hands back (a Mail.tm attachment, say) is only fetched
  if it is on that service's own host; anything else is refused before the inbox's token is attached.
- Every answer is size-capped (16 MB) and must be JSON; anything else is refused rather than parsed.
- The URLs Fastmail's session names (API, upload, download) must be HTTPS before the token is sent to them.
- No cookies, no telemetry, and no server of DarkCipher's own. The app connects only to Fastmail, the services you set
  up or turn on, and remote images in a message if you choose to load them.

## Other services

Each service is one small file that declares what it is, what it can do, its settings and any caution; Settings and
the app only offer what is declared, and a unit test checks each declaration against the code.

| Service | Trusted with | Notes |
|---|---|---|
| SimpleLogin, addy.io, Firefox Relay | An API key that manages your aliases | Official, documented APIs |
| DuckDuckGo | A token that can create @duck.com addresses on your account | **Unofficial**: the call DuckDuckGo's own extension makes (as password managers do). Create only; the app keeps its own encrypted list. Shown with a caution in Settings. |
| Forward Email | An API token for your domains (aliases and outbound mail) | Official API, open-source service |
| Cloudflare Email Routing | An API token | Settings asks for a token scoped to Email Routing rules (edit), destination addresses (read) and zones (read), never the Global API Key |
| ImprovMX, Migadu | An API key for your domains | Official APIs |
| mailcow | An administrator API key for your own server | Settings asks for a read-write key limited to your IP address; HTTPS only |
| Mail.tm / Mail.gw, TempMail.lol, DropMail | Nothing of yours: a random per-inbox password, token or session the app makes | **Off until you tick them**, with a caution that the service can read the mail. TempMail.lol and DropMail take their token in the request address (their API's design; over HTTPS). |

Every key, token, inbox password and session is stored only in the encrypted settings, like the Fastmail token.

**Left out for security** (with the reason, also shown in Settings): public disposable inboxes that anyone can read
by guessing the name (Guerrilla Mail, Maildrop, Mailinator, Mailsac), Guerrilla Mail's plain-HTTP API, and services
without a public, documented API (TrashMail.com, iCloud Hide My Email, 33mail and others).

**Add-ons and services:** an add-on can create an address on any set-up service only with your approval each time;
the approval shows the service, domain, where mail goes and the service's caution.

## Reading and writing mail

- **Viewer:**
  - scripts are disabled;
  - the CSP is `default-src 'none'`, so nothing loads except `data:` images unless you press **Load images**;
  - the HTML is sanitized (scripting elements, event handlers and script URLs removed, entity-decoded first);
  - permission requests and downloads are refused;
  - every link shows its real address and asks before it opens in your browser.
- **Composer:**
  - the editor's own script runs under a random per-load CSP nonce, so nothing in quoted or pasted HTML can execute;
  - quoted HTML is cleaned again with an inert DOM parser;
  - remote images in quotes don't load (no tracking pixels on reply).
- Attachments are saved only when you choose to, through a Save dialog.

## Add-ons

They use the same model as DarkCipher ServerManagement. [ADDONS.md](ADDONS.md) has the details.
- **Sandbox:** a separate program at Low integrity, in a Job object (512 MB, 8 processes, killed with the app), with a
  whitelisted environment.
  - Its process can't be opened by other processes.
  - Only its three pipes are inherited.
  - There is no unsandboxed fallback.
- **Encrypted bridge:** ECDH P-256, then HKDF-SHA256, then AES-256-GCM in each direction, with counter nonces and fresh
  keys every run. A plain-text, changed, replayed or reordered line ends the run.
- **Approval:** each permission is shown with its risk, and only ticked ones are granted. Changes and sends an add-on
  asks for are shown to you first. Updates can't gain permissions silently, and downgrades are refused.
- **Integrity:** files are hash-checked before every run, and a changed add-on is disabled. Theme add-ons are sealed on
  disk.
- **Never exposed to add-ons:** API keys, the master password, 2FA secrets and the store.

## Store (MSIX) edition

- **Signing:** Microsoft signs the package after certification. It is a full-trust desktop app (`runFullTrust`), the
  same binaries as the MSI.
- **Data folders:** write virtualization is off (`unvirtualizedResources`). The encrypted store stays in
  `%APPDATA%\DarkCipher\MaskedMail`, with the same protected access list as the MSI. It is shared between editions and
  isn't deleted on uninstall.
- **Start with Windows:** a declared startup task. Windows shows it, and you control it, in Task Manager → Startup apps.

## The process itself

- **DLL loading:** DLLs from network locations or carrying a Low integrity label are refused (image-load policy), and
  the current directory is not searched for DLLs. Every P/Invoke in every assembly resolves its Windows DLL from
  System32 only (`DefaultDllImportSearchPaths`), so a planted DLL next to the exe is never loaded.
- **Heap:** the process terminates on heap corruption.
- **Single instance:** one copy per Windows user. The installer can ask it to exit (`--exit`) before it changes files.
- **Errors:** an unexpected error on any thread is written to the sealed `errors.log`. One on the UI thread is also
  shown (once per 10 s for a repeat) and the app keeps running.

## How the code is kept honest

- **Analyzers as a gate.** Every project builds with the .NET analyzers at `latest`, every security rule on
  (`AnalysisModeSecurity=All`), and every warning treated as an error. There are two suppressions, each in the code
  with its reason:
  - the TLS 1.2/1.3 floor (CA5398);
  - HMAC-SHA1 in TOTP, which RFC 6238 authenticator apps require (CA5350).
- **Supply chain.** Every NuGet version is pinned centrally and locked (`packages.lock.json`; the build restores in
  locked mode). Every package, direct or transitive, is audited against the NuGet vulnerability database, and any
  known advisory fails the build. There are three runtime packages: WebView2, ProtectedData and QRCoder.
- **Small projects with one-way references.** Storage and crypto (`DcxMail.Core`) know nothing about mail, UI or
  add-ons. The add-on host has no UI: approvals go through a seam whose default is "refuse".
- **Tests.** The unit tests, and the exe self-test for the UI, gate every build (see [TESTING.md](TESTING.md)).

## What MaskedMail can't protect against

- **An unlocked PC:** someone using it while the app is open and unlocked. Use auto-lock, lock with Windows, 2FA or
  sandbox mode.
- **Malware running as you:** it can read the memory of a running, unlocked app.
- **The services themselves:** Fastmail and the alias services see what their APIs handle.
- **The website installer isn't code-signed**, so Windows SmartScreen warns on first run. Check the SHA-256 against
  `SHA256SUMS.txt`. The Microsoft Store edition is signed by Microsoft after certification. It is prepared but not
  submitted yet.

## Reporting a problem

Open an issue on the project, or contact darkcipher0x privately for anything sensitive. Please don't include real
email addresses, tokens or mail content in reports.
