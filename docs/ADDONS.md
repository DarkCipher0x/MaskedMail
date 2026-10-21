# DarkCipher MaskedMail add-ons (API 2)

MaskedMail add-ons use the same model and the same SDK as DarkCipher ServerManagement add-ons.

## How an add-on runs

### Separate programs

An add-on is a separate program, either a `.ps1` script or an `.exe`. It is started for one command and never loaded
into the app, so a crash ends the add-on, never the app.

### The sandbox

The program runs at **Low integrity** inside a **Job object**. Its limits are:
- 512 MB of memory and 8 processes;
- it is killed with the app;
- it gets a whitelisted environment;
- its only writable folder is `%USERPROFILE%\AppData\LocalLow\DarkCipher\MaskedMail\addons\<id>` (`$env:DCX_ADDON_DATA`).

Its process and thread have a protected DACL, so no other process can read its memory, duplicate its handles or
inject code into it. Only stdin, stdout and stderr are inherited.

There is no unsandboxed fallback.

Low integrity can still read many files and use the network. Install only add-ons you trust.

### Encrypted bridge

1. The app and the add-on exchange ephemeral P-256 keys. These hello lines are the only plain-text lines.
2. HKDF-SHA256 then gives one AES-256-GCM key for each direction.
3. Every later line is `base64(ciphertext || tag)`, with an implicit counter nonce.

A changed, replayed, reordered or plain-text line ends the run. `sdk/DcxBridge.dll` and `sdk/DcxAddon.ps1` do all of
this for PowerShell 5.1.

### Packages

A package is a `.dcxaddon` file: a zip with `addon.json` at its root. When you inspect one, the app refuses:
- zip-slip paths;
- more than 500 files, or more than 50 MB;
- API 1 (the old unencrypted bridge);
- unknown permissions.

### Approval

The approval screen shows:
- the publisher and version;
- the package's SHA-256;
- the program's Authenticode state;
- each permission and its risk.

Only the permissions you tick are granted. An update keeps your earlier grants, and any new permission starts
ungranted. Downgrades are refused.

### Integrity

The SHA-256 of every file is recorded in the encrypted settings and checked before every run. If a file has changed,
the add-on is disabled.

Theme add-ons are data only. Their files are AES-256-GCM-encrypted on disk with the store key and checked every time the
theme is applied.

## addon.json

```json
{
  "api": 2,
  "id": "com.example.my-addon",
  "name": "My add-on",
  "version": "1.0.0",
  "publisher": "Me",
  "description": "What it does",
  "entry": "run.ps1",
  "timeoutSeconds": 60,
  "permissions": ["aliases.read"],
  "commands": [ { "id": "summary", "title": "Summary", "context": "global" } ]
}
```

A command's `context` decides what it is told about when it runs:

| Context | Runs with | `invoke.params.context` |
|---|---|---|
| `global` | nothing selected | — |
| `alias` | the selected masked address | `alias.email`, `service`, `enabled`, `note`, `site` |
| `message` | the open message | `message.id`, `subject`, `from` |

## Methods

Call a method with `Invoke-Dcx '<method>' @{ ... }`.

| Method | Permission | What it does |
|---|---|---|
| `app.info`, `app.capabilities`, `ui.notify` | — | Version, grants, the method list, a status-bar line |
| `aliases.list` | `aliases.read` | Masked addresses (never API keys) |
| `aliases.create` / `setEnabled` / `update` | `aliases.write` | Changes; you see the exact change and allow or deny it. `aliases.create` takes `service` (any set-up service key, as `serviceKey` in `aliases.list`), `note`, `site`, `prefix`, `domain` and `forwardTo`; the approval shows the service's caution, if it has one. Only what the service supports is accepted. |
| `throwaway.create` | `aliases.write` | A throwaway address (`minutes`, `note`, optional `service`), which you approve; returns its email and expiry (capped to what the service allows) |
| `throwaway.list` | `aliases.read` | Your throwaway addresses and when each expires |
| `mail.list` / `mail.get` | `mail.read` (HIGH) | Messages in Inbox, Sent or an address's folder |
| `queue.add` | `queue.write` | Adds an email to the outgoing queue; nothing is sent |
| `mail.send` | `mail.send` (HIGH) | Sends now; you see the sender, recipients and subject, then allow or deny |
| `routing.sweep` | `routing.run` | Files Inbox mail into folders now |
| `files.pick` | `filesystem.read` | A file you pick in a dialog (≤ 2 MB) |

Add-ons can never reach:
- API keys;
- the master password;
- 2FA secrets;
- the encrypted store.

Every run and every approved or denied action is written to the security log.

## Theme add-ons

Leave out `entry` and set `"theme": "theme.json"`. The theme file may contain only these fields:
- `colors`: palette keys (Bg, Surface, Input, Fg, Muted, Line, Hover, Track, ScrollThumb, HeaderBg, HeaderFg, Good, Warn,
  Bad, Accent, AccentHover, OnAccent, Brand), each `{"dark":"#RRGGBB","light":"#RRGGBB"}`;
- `name`;
- `notes`.

Anything else is refused.

## Packing

```
powershell -ExecutionPolicy Bypass -File samples\addons\pack.ps1 samples\addons\masked-report
```

This writes `dist\addons\<id>\<id>-<version>.dcxaddon`, a `.sha256` file and `dist\addons\<id>.zip`. Earlier versions are
kept.
