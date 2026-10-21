# DarkCipher MaskedMail — privacy policy

*Applies to DarkCipher MaskedMail for Windows: the website installer and the Microsoft Store edition. Last updated
2026-10-20.*

## The short version

- **MaskedMail has no servers, no accounts and no telemetry.** DarkCipher Systems receives nothing from it, ever.
- **Everything it stores stays on your PC, encrypted.**
- **It talks only to Fastmail and the alias or inbox services you set up or turn on**, to do what you ask.

## What the app stores, and where

Everything is on your PC, in folders only your Windows account can open:

- **Your keys:** your Fastmail API token and the keys or tokens of any other service you set up (SimpleLogin, addy.io,
  Firefox Relay, DuckDuckGo, Forward Email, Cloudflare, ImprovMX, Migadu, mailcow, DropMail).
- **Disposable inboxes you made** (Mail.tm, TempMail.lol, DropMail): the address and the random password, token or
  session the app uses to read it. Their mail stays at that service.
- **DuckDuckGo addresses you made**, because DuckDuckGo can't list them for the app.
- **Your settings**, including the list of folders for your masked addresses and your throwaway addresses with their
  expiry times.
- **Your outgoing queue:** emails you queued and haven't sent yet.
- **A security log** of sign-ins, locks and security changes. It never contains keys, passwords or message content.
- **An error log** of unexpected app errors (the error type, message and code location), so a problem can be
  diagnosed. It stays on this PC, encrypted, and is never sent anywhere.
- **A browser profile** used to display messages. The app writes no mail content to disk.

All of it is encrypted with AES-256-GCM. The key is protected by your Windows account and, if you set one, by your
master password. **Your mail itself stays with your email provider (Fastmail, or the disposable inbox service)**: the
app fetches a message when you open it and keeps no copy.

## What the app sends, and to whom

| Where it goes | What is sent | When |
|---|---|---|
| **Fastmail** (`api.fastmail.com`) | Your API token, plus the requests you make: listing, creating and changing masked addresses, reading, moving and sending mail, creating folders | When you use those features, and on the filing timer for addresses you route |
| **Masked-email services**: SimpleLogin, addy.io, Firefox Relay, DuckDuckGo (only if you set them up) | Your key or token and the requests you make (list, create, change, delete, reverse aliases, activity) | When you use those features |
| **Your domain services**: Forward Email, Cloudflare, ImprovMX, Migadu, your mailcow server (only if you set them up) | Your key or token and the address requests you make; for Forward Email, also mail you send from its addresses | When you use those features |
| **Disposable inbox services**: Mail.tm / Mail.gw, TempMail.lol, DropMail (only if you turn them on) | A request for a new inbox, then the per-inbox password, token or session to read it. **These services hold and can read the mail sent to those inboxes.** | When you make or open one, and while its inbox window checks for mail |
| **Websites named in a message** | Only if you press **Load images**, turn on remote images, or open a link (after confirming its address) | Your choice only |

Nothing else is sent. There is no analytics, crash reporting, advertising or "phone home". The app doesn't check for
updates over the internet: Store installs are updated by the Microsoft Store.

All connections use HTTPS (TLS 1.2 or 1.3). The app refuses to follow redirects, so your keys can't be sent anywhere
else.

## Add-ons

Add-ons are optional programs you choose to install. They run in a restricted sandbox and receive only what you grant
them on the approval screen; anything they ask to change or send is shown to you first. An add-on is a program written
by its publisher, and it can use the network itself, so install only add-ons you trust. DarkCipher Systems receives
nothing from add-ons.

## Your choices

- **See what's stored:** Settings shows everything the app keeps. The security log lists security events.
- **Export:** an encrypted backup (`.dcxbackup`) holds your keys and settings.
- **Delete:**
  - Remove any key in Settings.
  - Uninstalling the website edition lets you delete all app data.
  - In the Store edition, delete the `%APPDATA%\DarkCipher\MaskedMail` folder after uninstalling.
  - Your masked addresses and mail are kept by your email and alias providers. Manage or delete them there, or from
    the app before you uninstall.

## Children

MaskedMail is a general-purpose email tool. It is not directed at children and collects nothing from anyone.

## Changes and contact

Changes to this policy are published with the app's release notes and on the DarkCipher Systems website. Questions:
use the contact details at <https://darkciphersystems.com>.
