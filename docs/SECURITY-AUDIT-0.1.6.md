# DarkCipher MaskedMail — security audit and hardening report (0.1.6)

**Date:** 2026-10-19 · **Version audited:** 0.1.5 · **Fixed in:** 0.1.6 · **Auditor:** Claude (Opus 5.5), with the
owner (darkcipher0x).

## 1. Scope and method

**What was reviewed:** the full application source, line by line, covering:
- storage: SecureStore, settings, key ring, outgoing queue, security log and backups;
- networking: the Fastmail JMAP client and the SimpleLogin, addy.io and Firefox Relay clients;
- the HTML message viewer and the HTML composer (WebView2);
- sign-in and app lock: master password, TOTP 2FA, auto-lock and sandbox mode;
- the add-on system: sandbox process, encrypted bridge, packages and permissions;
- the installer hooks (`--exit`, `--cleanup`).

**How it was reviewed:** each area was read from an attacker's point of view, with these attackers in mind:
- a hostile email (sender-controlled HTML) opened or replied to;
- a hostile or compromised mail server, or the network path to it;
- a malicious add-on;
- another account, or malware running at Low integrity, on the same PC;
- someone with a copy of the disk or a backup;
- someone at the unlocked PC;
- a screen recording or live stream.

**How the fixes are verified:** every fix has a self-test check that fails without it (section 5). The self-test runs
against in-process fake servers and draws windows off-screen. No real account was used for the security checks.

## 2. Summary

| # | Finding | Severity | Status |
|---|---|---|---|
| F1 | Quoted mail could run script in the composer (regex sanitizer bypass + scripts enabled) | **High** | Fixed |
| F2 | HTTP redirects were followed; SimpleLogin's `Authentication` header would follow a redirect to another host | **High** | Fixed |
| F3 | URLs from the Fastmail session (api/upload/download) weren't required to be HTTPS | Medium | Fixed |
| F4 | WebView2 content could request permissions, start downloads, use swipe navigation and script dialogs | Medium | Fixed |
| F5 | Data folders inherited default permissions | Medium | Fixed |
| F6 | Security log: deleted or reordered lines weren't detected | Low | Fixed |
| F7 | Wrong-password back-off reset when the app restarted | Low | Fixed |
| F8 | Password-derived keys stayed in memory after use | Low | Fixed |
| F9 | Master/backup password rule was length only | Low | Fixed |
| F10 | 2FA QR/key, recovery codes and password prompts were visible to screen capture and streaming | Low (High while streaming) | Fixed |
| F11 | Recovery codes copied to the clipboard stayed there | Low | Fixed |
| F12 | Quoting a message in the composer loaded its remote images (tracking pixels fire on reply) | Low (privacy) | Fixed |
| F13 | No process-level mitigations (DLL planting from Low-integrity or remote files) | Low | Fixed |

There were no critical findings. These were already sound and remain unchanged:
- **Encryption at rest:** AES-256-GCM, with each file bound to its name.
- **Key ring:** the key is wrapped by DPAPI and/or the master password (PBKDF2-SHA256, 600,000 iterations).
- **TOTP:** the secret is sealed, codes can't be replayed, and recovery codes are stored as hashes.
- **Message viewer:** scripts are disabled, there is a strict CSP, and remote content is blocked by default.
- **Add-ons:** the Low-integrity sandbox, the encrypted bridge and the hash checks.

## 3. Findings and fixes

### F1 — Script execution from quoted mail in the composer (High)

**Problem:**
- The composer is a WebView2 page and needs scripts for formatting.
- When you replied or forwarded, the original HTML was cleaned by `HtmlTools.Sanitize` (regular expressions) and then
  inserted into the editor.
- The regex missed handlers separated by `/` (`<img/onerror=…>`), SVG event handlers, `srcdoc`, `formaction` and
  entity-encoded `javascript:` URLs.
- The page's CSP allowed `'unsafe-inline'` scripts.

**Impact:** a crafted email, when replied to, could run script in the editor. That script could post the editor's
"send" message, or change the reply's content.

**Fix (three independent layers):**
1. **CSP nonce.** The editor's CSP is now `script-src 'nonce-<random per load>'`. Only the editor's own `<script>` runs.
   Inline handlers and `javascript:` URLs can't execute, whatever reaches the page.
2. **DOM sanitizer.** HTML from outside (quotes, queued messages) is parsed inertly with `DOMParser` and cleaned before
   it is inserted:
   - scripting and embedding elements are removed;
   - every `on*` attribute, `srcdoc` and `formaction` is removed;
   - `javascript:` / `vbscript:` / `data:text/html` URLs are removed, checked after the browser's own decoding.
3. **Stronger server-side sanitizer**, used by both the viewer and the quote path:
   - handlers are found after a space, `/` or quote;
   - `svg`, `math`, `template` and `portal` are removed;
   - `srcdoc` and `formaction` are removed;
   - link-like attributes are entity-decoded and stripped of whitespace before they are checked;
   - CSS `expression()`, `behavior` and `-moz-binding` are neutralised.

The viewer was not exploitable: its scripts are disabled and its CSP is `default-src 'none'`. It gets layer 3 as well.

**Verified by:** `html: sanitizer stops known bypasses…` (13 vectors) and `editor: only its own script may run…`.

### F2 — Credentials could follow a redirect (High)

**Problem:** the HTTP clients used default handlers, which follow redirects. .NET strips the standard
`Authorization` header on a redirect, but SimpleLogin's key travels in a custom `Authentication` header, which is
forwarded. A compromised or misconfigured server, or a self-hosted URL with a redirect, could forward the key to
another host. A redirect could also downgrade to `http://`.

**Fix:** every connection now goes through `Services/Net.cs`:
- redirects are never followed and are reported as an error;
- only `https://` requests are sent, checked before each request leaves the app;
- TLS 1.2/1.3 only, with the system's certificate validation (never overridden);
- no cookies, a 20 s connect timeout, bounded headers, and a 64 MB response cap.

**Verified by:** `net: redirects are refused and the API key never reaches the other host; http:// is refused`.

### F3 — Session-provided URLs not checked (Medium)

**Problem:** Fastmail's JMAP session tells the client where to send API calls, uploads and downloads, and the bearer
token goes to all of them. None was required to be HTTPS.

**Fix:** the session URL and the `apiUrl`, `uploadUrl` and `downloadUrl` it names must all be `https://`. Otherwise
nothing is sent. Settings also refused non-HTTPS server addresses before this change.

**Verified by:** `jmap: a session naming a plain-http API URL is refused before the token is sent there`.

### F4 — WebView2 not locked down (Medium)

**Fix:** in both the viewer and the editor:
- every permission request is denied (camera, microphone, location, notifications, clipboard and so on);
- downloads are cancelled (attachments are saved only through the app);
- basic-auth prompts and certificate-error pages are refused;
- swipe navigation, script dialogs and the built-in error pages are disabled.

These were already in place: dev tools, the status bar, password autosave and autofill are off; host objects are
off; navigation and new windows are intercepted; and links go out only through a confirmation that shows the real
address.

### F5 — Folder permissions (Medium)

**Fix:** `%APPDATA%\DarkCipher\MaskedMail` (settings, key ring, queue, log) and `%LOCALAPPDATA%\DarkCipher\MaskedMail`
(viewer profile, add-on programs) now have a protected access list. Only the signed-in user and SYSTEM have access,
and nothing is inherited. This is a second layer: the files were already encrypted.

**Verified by:** `storage: the data folder is locked to this Windows user`.

### F6 — Security log integrity (Low)

**Fix:** each log entry now holds a SHA-256 of the previous stored line, inside its own encryption. When the log is
read, every link is checked. A removed, reordered or inserted line shows as "⚠ log altered" at that point.

**Limits:** cutting lines off the end, or deleting the whole file, can't be detected without an external anchor. The
oldest retained line can't be checked after the log is trimmed to 5,000 entries.

**Verified by:** `security log: removing a line from the middle is detected (hash chain)`.

### F7 — Sign-in throttling across restarts (Low)

**Fix:** wrong passwords and codes are now counted in the key ring. From the 4th failure in a row, each attempt waits
2^(n−3) seconds, up to 5 minutes, and restarting the app doesn't reset the count. A correct sign-in does. The
per-window delay is kept as well.

This slows down someone guessing at the keyboard. An attacker with a copy of the files faces PBKDF2 at 600,000
iterations instead.

**Verified by:** `sign-in: wrong attempts are counted across restarts, back off, and reset on success`.

### F8 — Key material in memory (Low)

**Fix:** key-encryption keys derived from a password are zeroed immediately after use when unlocking, verifying and
setting a password. The data key is zeroed when the app exits or is quit from the lock screen. Strings typed into
password boxes are managed by .NET and can't be wiped reliably; this is a known limit.

### F9 — Password rules (Low)

**Fix:** master and backup passwords must:
- have 12 or more characters, with at least 4 different ones;
- not be a common password with digits or symbols added;
- mix kinds of character (letters with digits or symbols), unless they are a passphrase of 16 or more characters.

**Verified by:** `passwords: weak … refused, passphrases and mixed passwords accepted`.

### F10 — Screen capture and streaming (Low; High while streaming)

**Fix:** these windows are always excluded from capture (`SetWindowDisplayAffinity`, `WDA_EXCLUDEFROMCAPTURE`):
- the sign-in and lock screen;
- the 2FA set-up (QR code and key);
- recovery codes;
- every password and backup-password prompt.

They show black or nothing in OBS, Discord, Teams, screenshots and remote viewers. A new setting, Settings → Security
& privacy → **Hide MaskedMail from screen capture**, hides every window.

**Verified by:** the render check confirms the sign-in window is excluded and the main window follows the setting.

### F11 — Clipboard (Low)

**Fix:** recovery codes copied to the clipboard are cleared after 60 seconds if they are still there.

### F12 — Tracking pixels when replying (Low, privacy)

**Fix:** the composer's CSP allows `data:` images only, unless you enabled remote images in Settings. Quoting a
newsletter no longer contacts its tracker. The images are still referenced in what you send.

### F13 — Process mitigations (Low)

**Fix:** at start-up the app turns on these mitigations:
- the image-load policy refuses DLLs from remote locations and DLLs labelled Low integrity, so a Low-integrity add-on
  can't plant one;
- the current directory is removed from the DLL search path;
- the process terminates if its heap is corrupted.

Dynamic-code and extension-point policies were not enabled, because they break .NET's JIT and input methods.

**Verified by:** `process: DLLs from remote or Low-integrity-labelled files are refused`.

## 4. Residual risks (accepted, documented)

### Someone at the unlocked PC

While the app is unlocked, its data is available to anyone using the PC. Mitigations:
- auto-lock;
- lock when Windows locks;
- sandbox mode;
- 2FA.

### Malware running as you, at Medium integrity

Such malware can read the app's memory and the DPAPI-protected key. A master password required at start keeps the key
off disk. It doesn't protect a running, unlocked app. No desktop app can fully defend against this.

### The installer isn't code-signed

Windows SmartScreen warns on first run. Signing needs a certificate. The Microsoft Store route (free signing) is
available, as for ServerManagement.

### Add-ons

Low integrity limits what an add-on can write, but it can still read many files and use the network. Permissions
govern what the app gives an add-on, not everything its program can do. The approval screen says so.

### Mail providers

The app trusts Fastmail, SimpleLogin, addy.io and Mozilla with whatever their APIs see. Your mail stays at Fastmail,
not on this PC.

### Remote images

Loading remote images in a message, or turning on "load remote images", tells senders that you opened their mail. It
is off by default.

### Not yet tried with real accounts

None of the security changes in this report has been exercised against a real account.

## 5. Verification

```
DcxMaskedMail.exe --selftest <report.txt>     (build.ps1 runs it and refuses to package on any failure)
```

0.1.6 result: **44 ok, 0 failed**, including 9 checks added by this audit and 5 add-on checks that run real
PowerShell add-ons in the real sandbox. The full list is in [TESTING.md](TESTING.md).
