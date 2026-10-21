# DarkCipher MaskedMail — user guide

MaskedMail creates and manages **masked email addresses**: a different address for every site, so your real one never
leaks. It gives each address its own folder in Fastmail and files its mail there. It also works as a lightweight HTML
email client that can send from any of those addresses.

Contents:
1. [Install](#1-install)
2. [First set-up](#2-first-set-up)
3. [The main window](#3-the-main-window)
4. [Masked addresses](#4-masked-addresses)
5. [Folders and routing](#5-folders-and-routing)
6. [Reading mail](#6-reading-mail)
7. [Writing mail](#7-writing-mail)
8. [The outgoing queue](#8-the-outgoing-queue)
9. [Security & privacy](#9-security--privacy)
10. [Backups](#10-backups)
11. [Add-ons](#11-add-ons)
12. [Settings reference](#12-settings-reference)
13. [Keyboard shortcuts](#13-keyboard-shortcuts)
14. [Uninstall](#14-uninstall)
15. [Troubleshooting](#15-troubleshooting)

---

## 1. Install

1. Run `DarkCipherMaskedMail-<version>-x64.msi`. Windows may warn that the installer isn't signed. Check its SHA-256
   against `SHA256SUMS.txt` next to it.
2. Accept the licence and choose the folder. The default is `C:\Program Files\DarkCipher Systems\MaskedMail`.
3. Options:
   - a desktop shortcut;
   - installing the Microsoft Edge WebView2 Runtime, which is ticked only if it's missing. Windows 11 already has it.
4. Finish, and MaskedMail starts.

Later versions install over the old one and keep your settings. If MaskedMail is running in the notification area,
the installer closes it first.

**Microsoft Store edition.** When it's available, install it from the Microsoft Store instead.
- It's the same app, signed by Microsoft, so there's no warning. The Store updates it.
- It uses the same encrypted data folder as the website installer, so you can switch between them without losing your
  settings.
- **Start with Windows** appears in Task Manager → Startup apps.
- It needs Windows 10 version 1903 or later.

## 2. First set-up

1. **Fastmail API token.**
   1. In Fastmail, go to **Settings → Privacy & Security → Manage API tokens** and create a token with:
      - Masked Email;
      - Email (read/write);
      - Email submission.
   2. In MaskedMail, open **⚙ Settings → Fastmail**, paste the token and press **Test connection**. It should say
      "signed in as …".
2. **Other services (optional).** Settings has three pages for them (see [Services](#services)):
   - **Masked-email services:** SimpleLogin (also your Proton Pass aliases), addy.io, Firefox Relay, DuckDuckGo.
   - **Your own domains:** Forward Email, Cloudflare Email Routing, ImprovMX, Migadu, mailcow.
   - **Disposable inboxes:** Mail.tm / Mail.gw, TempMail.lol, DropMail. These are off until you tick them.

   Fill in a service's card and press **Test connection**.
3. **Save.** Your masked addresses appear on the left.
4. **Secure it.** This is recommended; see [section 9](#9-security--privacy):
   - make an encrypted backup;
   - set a master password;
   - turn on 2FA.

## 3. The main window

- **Header:**

  | Button | What it does |
  |---|---|
  | **＋ New masked email** | Create a masked address |
  | **⏱ Throwaway ▾** | A disposable address that ends itself, with a live inbox |
  | **✉ New email** | Write an email |
  | **📤 Queue** | The outgoing queue |
  | **◇ Add-ons ▾** | Run or manage add-ons |
  | **🔒** | Lock now |
  | **⚙ Settings** | All settings |
  | Theme | System, dark or light |

- **Left:** Inbox, Sent and your masked addresses, with search and filters by service and state. A green dot means
  enabled, red means disabled, and 📁 routed means it has a folder.
- **Middle:**
  - the selected address's card: copy, email, enable/disable, edit, folder and delete;
  - below it, the messages in its folder.
- **Right:** the message viewer.
- **Status bar:**
  - routing status and **⟳ sweep now**;
  - the encryption badge; click it to open Security.

Closing the window keeps MaskedMail running in the notification area, so mail keeps being filed. Right-click the tray
icon for: Open, New masked email, Sweep now, Outgoing queue, Lock now and Exit.

## 4. Masked addresses

**＋ New masked email** opens a form. It only shows what the chosen service can do:
- **Service:** any service you have set up that can create addresses. Disposable inboxes are marked as such, and a
  service's caution, if it has one, is shown before anything is created.
- **What is it for?** A note for yourself.
- **Website:** optional.
- **Address starts with:** optional, for services that let you choose.
  - Fastmail allows a–z, 0–9 and `_`, then adds random characters.
  - SimpleLogin gives you `prefix` plus the suffix you pick.
  - Every other service gives you exactly `name@domain`.
  - Left empty, a random, unguessable name is used (or the service picks one).
- **Domain:** for services with several (addy.io domains, SimpleLogin suffixes, your own domains, disposable domains).
- **Its mail goes to:** for services with a choice (SimpleLogin mailboxes, addy.io recipients, verified Cloudflare
  destinations, the forward-to addresses you set).
- **Create its folder and file its mail there:** ticked by default. It isn't shown for disposable inboxes, whose mail
  stays at their service and is read in the app.

The new address is copied to the clipboard.

On an address's card, or by right-clicking it in the list, you only see what its service supports:
- **Copy** the address.
- **Email** from it (services that can send).
- **Enable / Disable.**
- **Edit** the note (and website, where the service keeps one).
- **Create folder / Stop routing** (not for disposable inboxes).
- **📊 Activity:** recent deliveries, as reported by SimpleLogin or ImprovMX.
- **Delete.** Fastmail keeps a deleted address, and mail sent to it bounces; elsewhere it is removed. For DuckDuckGo
  this is **Remove from list**: DuckDuckGo has no way to delete through its interface, so the address keeps
  forwarding until you turn it off from the DuckDuckGo footer of a forwarded email.

### Services

| Service | Kind | What the app can do | Send from it? |
|---|---|---|---|
| **Fastmail** | masked | list, create (chosen start), note and website, disable, delete | Yes, directly |
| **SimpleLogin** (and Proton Pass aliases) | masked | list, create (random or chosen name + chosen suffix, chosen mailbox), note, disable, delete, activity | Yes, through a reverse alias |
| **addy.io** | masked | list, create (chosen domain, recipient and name style, or a custom name), note, disable, delete | Yes, `alias+recipient=domain@…` (paid plans) |
| **Firefox Relay** | masked | list, create, note and website, disable, delete | No |
| **DuckDuckGo** (@duck.com) | masked | create; the app remembers the ones it made; note | No |
| **Forward Email** | own domain | list, create (chosen name and domain), note, turn off (drop, bounce or defer its mail), delete | Yes, by Forward Email itself |
| **Cloudflare Email Routing** | own domain | list, create (chosen name and zone, verified destination), note (rule name), turn off, delete | No |
| **ImprovMX** | own domain | list, create (chosen name and domain), delete, delivery log | No |
| **Migadu** | own domain | list, create (for a Migadu mailbox on the same domain), delete | No |
| **mailcow** (your server) | own domain | list, create, note, turn off, delete, and time-limited aliases the server ends by itself | No |
| **Mail.tm / Mail.gw** | disposable | create (chosen name and domain), read, mark read/unread, delete messages, save attachments, delete the inbox | No |
| **TempMail.lol** | disposable | create (chosen start), read; ends by itself after about an hour | No |
| **DropMail** | disposable | create (chosen domain), read; ends about 10 minutes after the last check | No |

**Disposable inboxes are for things that don't matter.** The service holds the mail and can read it, the domains are
often refused, and the inbox can vanish. Never use one for banking, an account you'd need to recover, or anything
personal. Each inbox's password, token or session is kept encrypted by the app; nobody else has it.

**DuckDuckGo uses an unofficial interface:** the call DuckDuckGo's own extension makes (password managers use it too).
It works today but could change. Treat its token like a password.

**Services that are not included, and why** (also listed in Settings → Disposable inboxes):
- **Proton Pass:** its aliases are SimpleLogin aliases, so add your SimpleLogin key.
- **Apple iCloud Hide My Email:** no public API.
- **Guerrilla Mail, Maildrop, Mailinator and Mailsac (public inboxes):** anyone who knows or guesses an address can
  read its inbox, so a password-reset link sent there could be read by a stranger.
- **TrashMail.com:** its API isn't publicly documented.
- **33mail, Spamgourmet, Cloaked, Burner Mail, Blur, Purelymail:** no public, documented API for managing addresses.

### Throwaway addresses

A throwaway is a masked address that ends itself. Use one for a one-off sign-up, a download link, a Wi-Fi portal or a
trial.

**Making one:**
- Click **⏱ Throwaway ▾** in the header and pick a lifetime: 10 minutes, 1 hour, 1 day, 7 days, 30 days or a custom
  one (`45m`, `6h`, `3d`).
- **Ctrl+Shift+T**, or **New throwaway address** in the tray menu, makes one with your default lifetime.

**What happens:**
1. A random address is created with its own folder, and copied to the clipboard.
2. Its **live inbox** opens. It checks for mail every 10 seconds and picks out **verification codes** (click to copy)
   and **confirmation links** (open or copy). Footer, social and image links are skipped.
3. In the address list it shows **⏱ time left**. Its card has **📥 Inbox**, **+1 hour**, **Keep it** and **🔥 Burn
   now**.

| Action | What it does |
|---|---|
| **+1 hour / +1 day** | Later expiry |
| **Keep it** | Becomes a normal masked address that never expires |
| **🔥 Burn now** | Ends it straight away |

**When it expires:**
- By default the address is **deleted**, so mail sent to it bounces. Fastmail keeps it as "deleted".
- Its **mail goes to Trash** and its **folder is removed**. Trash keeps the mail for a while; it isn't destroyed.
- You can change this in **Settings → Throwaway addresses**: only disable the address instead, or keep its mail and
  folder.
- Expiry is checked every 30 seconds while MaskedMail runs (the tray is enough) and at every start. One that ran out
  while the PC was off ends at the next start.
- If the service can't be reached, it's retried, and the reason shows on the card.

Throwaways work with every service that can create addresses. Fastmail is used by default; choose another in
**Settings → Throwaway addresses** (an add-on can also name one). Some services change how a throwaway behaves:
- **mailcow:** the throwaway is a **time-limited alias** that your server deletes by itself when its time is up, even if
  MaskedMail never runs again. It can't be extended.
- **Disposable inboxes (Mail.tm, TempMail.lol, DropMail):** no Fastmail folder; the live inbox reads the service
  directly. TempMail.lol inboxes last at most about an hour and DropMail ones end ~10 minutes after the last check, so
  the lifetime is capped and **+1 hour** isn't offered. When it ends, a Mail.tm inbox is deleted at Mail.tm; the
  others end by themselves.
- **DuckDuckGo:** when it ends, it is only removed from the app's list (DuckDuckGo can't delete through its interface),
  and the security log says so. Turn it off from a forwarded email's DuckDuckGo footer.
- **Services without an off switch or delete** (ImprovMX and Migadu can delete; all can do one or the other) end in the
  strongest way they support, and the security log records what was done.

## 5. Folders and routing

Each address gets its own folder: `Inbox/automation/<address>`. You can change the parent folder in **Settings →
Folders & routing**.

Fastmail's API can't create rules, so MaskedMail files the mail itself:
- It checks the newest Inbox mail every 2 minutes (you can change this).
- It moves anything addressed to a routed address (To, Cc, Bcc or Delivered-To) into that address's folder.
- It keeps doing this while the app sits in the notification area, and while it's locked.
- **⟳ sweep now** files mail immediately.
- **Create folders for every existing address** (Settings → Folders & routing) sets up the addresses you already had.
- If you delete a folder in Fastmail, MaskedMail stops routing that address. Press **Create folder** to make it again.

For mail to be filed while you're signed in to Windows but haven't opened the app, turn on **Start with Windows**
(Settings → Appearance & behaviour).

## 6. Reading mail

- **Opening:** click a message to open it. It's marked read automatically; use **Mark unread** to undo that.
- **Remote images** are blocked so senders can't tell you opened their mail. Press **🖼 Load images** to see them for
  that message. Pictures sent inside the email always show.
- **Scripts in mail never run.**
- **Links** show you their real address before they open in your browser.
- **Attachments** are listed under the header. Click one to save it.
- **Reply / Reply all / Forward:**
  - replies go out from the masked address the message was sent to;
  - forwarding includes the attachments.
- **Delete** moves the message to Trash.

## 7. Writing mail

The composer is a lightweight HTML editor.

- **From:**
  - any enabled masked address that can send, or your own Fastmail addresses;
  - the line under it explains how the mail will go out.
- **To / Cc:** separate addresses with `,` or `;`.
- **Formatting:**
  - font and size;
  - bold, italic, underline and strikethrough;
  - text colour and highlight;
  - bulleted and numbered lists, indents, alignment and quotes;
  - links (🔗 or Ctrl+K), horizontal lines;
  - undo, redo and clear formatting.
- **Images:** use 🖼, paste them (Ctrl+V) or drag them in. Click an image, then ⅓, ½ or 1:1 to resize it. Images are
  sent inside the email.
- **Attachments:** 📎. Each one shows as a chip; click the chip to remove it.
- **Copies:** sends identical copies (1–50).
- **Separate copy per recipient:** everyone in To and Cc gets their own email and doesn't see the others.
- **Sending:**
  - **➤ Send** (Ctrl+Enter) sends now;
  - **⏸ Add to queue** (Ctrl+Shift+Enter) keeps the email for later.

Your default font and size, and the name shown on sent mail, are set in **Settings → Writing**.

## 8. The outgoing queue

Open it with **📤 Queue** or Ctrl+Shift+Q. The queue holds emails until you send them together.

| Action | What it does |
|---|---|
| ＋ New email… | Write an email and queue it |
| ✎ Edit… (or double-click) | Open it in the composer. **Update in queue** saves the changes; **Send** sends it now. |
| ⧉ Duplicate | A copy right after it |
| ×n Copies… | How many identical copies to send |
| 👥 One per recipient | Split into one email per person in To and Cc |
| ✉ Change sender… | Send from another address |
| ↑ ↓ | Change the order |
| ☐ Skip / include | Unticked items stay but aren't sent by **Send all** |
| 🗑 Remove | Delete from the queue |

- **Send all** sends the ticked emails in order. **Send selected** sends only the highlighted ones.
- **Pause between emails** (default 3 s) avoids tripping sending limits.
- **Stop at the first failure** is optional.
- **Stop** ends a run part-way.
- Sent emails leave the queue. Failed ones stay with their error, and anything already sent is never sent again.
- The queue is encrypted on disk and survives restarts.

## 9. Security & privacy

Everything is under **Settings → Security & privacy**. Click the 🔒 badge in the status bar to get there.

- **Encryption.** Everything the app stores is encrypted with AES-256-GCM: your Fastmail token, other API keys,
  folders, settings, the queue and the security log. By default the key is protected by your Windows account.
- **Master password.**
  - **Set / change master password…** adds a password. It must have 12+ characters, not be a common password, and mix
    kinds of character, unless it is a passphrase of 16+ characters.
  - With **Require it every time the app starts** ticked, the Windows copy of the key is removed and nothing opens
    without the password. **There's no recovery if you forget it**, so keep an encrypted backup.
- **Two-factor sign-in.**
  1. **Set up 2FA…** shows a QR code; scan it with your authenticator app.
  2. Enter the 6-digit code to confirm.
  3. **Save the 10 recovery codes.** Each works once. They're shown only once, and are cleared from the clipboard 60
     seconds after you copy them.
  - **New recovery codes…** re-enrols your authenticator with a new key.
  - **Turn off 2FA…** needs your password (if you have one) and a code.
- **App lock.**
  - **Lock after inactivity:** 5 minutes to 4 hours.
  - **Lock when Windows locks or the PC sleeps.**
  - **🔒 / Ctrl+Shift+L** locks now.
  - While locked, windows are hidden and mail keeps being filed.
- **Sandbox mode.** The master password is required at every start, auto-lock is always on, and the viewer's cache is
  wiped on exit.
- **Wrong attempts.** From the 4th wrong password or code in a row, each try waits longer, up to 5 minutes. Restarting
  the app doesn't reset this.
- **Privacy:**
  - **Load remote images automatically** is off by default.
  - **Hide MaskedMail from screen capture** makes every window black in OBS, Discord, Teams and screenshots. The
    sign-in screen, 2FA set-up, recovery codes and password prompts are always hidden.
- **Security log.** **Security log…** lists sign-ins, locks, security changes, backups, add-on actions and addresses
  created or deleted. Every line is encrypted, and removed lines are flagged as "⚠ log altered".

There's more detail in [SECURITY.md](SECURITY.md).

## 10. Backups

**Settings → Security & privacy → Encrypted backup…** writes a `.dcxbackup` file. It holds every API key and setting,
sealed with a backup password you choose. It doesn't depend on this PC, so it restores anywhere.

**Restore from backup…** replaces your keys and settings with the backup's. Your master password and 2FA stay as they
are.

## 11. Add-ons

Open **◇ Add-ons ▾ → Manage add-ons…**, or **Settings → Add-ons**.

- **Install add-on…:**
  1. Pick a `.dcxaddon` file.
  2. The approval screen shows the publisher, version, the package's fingerprint, its signature and each permission
     with its risk.
  3. Only the permissions you tick are granted.
- **Running commands:** from the **◇ Add-ons ▾** menu, or the add-on's page.
  - Commands marked "(selected address)" need a masked address selected.
  - Commands marked "(open message)" need a message open.
- **Anything an add-on changes or sends is shown to you first.** You allow or deny it.
- **Managing add-ons:** enable or disable, untick a permission to revoke it, use or leave a theme, **Check files**,
  read the **Run log**, or **Remove**.

There are two samples: **Masked address report** and the **Midnight blue** theme. Writing your own is covered in
[ADDONS.md](ADDONS.md).

## 12. Settings reference

| Category | Settings |
|---|---|
| Security & privacy | Master password, 2FA, app lock, sandbox mode, backups, security log, remote images, hide from screen capture |
| Fastmail | API token, JMAP session URL, test, link to Fastmail's token page |
| Masked-email services | SimpleLogin, addy.io, Firefox Relay, DuckDuckGo: key or token, server (self-hosted works), name style (addy.io), test |
| Your own domains | Forward Email, Cloudflare Email Routing, ImprovMX, Migadu, mailcow: key or token, where new addresses forward to, domains, test |
| Disposable inboxes | Mail.tm / Mail.gw, TempMail.lol, DropMail: "use this service" tick, server or token, test; services not included and why |
| Throwaway addresses | Service, default lifetime, delete or disable on expiry, clean up mail and folder, inbox check interval, active throwaways |
| Folders & routing | Routing on/off, parent folder, check interval, create folders for existing addresses |
| Writing | Your name on sent mail, default font and size |
| Appearance & behaviour | Theme, close to the notification area, start with Windows |
| Add-ons | Installed add-ons, Manage add-ons… |
| About | Version, data locations, what the app connects to |

Use the search box (Ctrl+F) to find any option. Security changes apply immediately; everything else applies when you
press **Save**.

## 13. Keyboard shortcuts

| Keys | Action |
|---|---|
| Ctrl+N | New email |
| Ctrl+Shift+N | New masked email |
| Ctrl+R | Reply |
| Del (in the message list) | Delete the message |
| F5 | Reload masked addresses |
| Ctrl+Shift+Q | Outgoing queue |
| Ctrl+Shift+T | New throwaway address (default lifetime) |
| Ctrl+Shift+L | Lock now |
| Composer: Ctrl+Enter / Ctrl+Shift+Enter | Send / add to queue |
| Composer: Ctrl+K | Insert link |
| Settings: Ctrl+F, Esc | Search settings, clear the search |

## 14. Uninstall

Use **Windows Settings → Apps → DarkCipher MaskedMail → Uninstall**, or the Start menu's **Repair or uninstall**
shortcut. You can choose to delete:
- **Encrypted settings:** keys, folder list, preferences, the queue and the security log, plus the add-ons' data
  folders.
- **Cache:** the viewer's browser profile and installed add-on files.

Nothing is deleted unless you tick it. Your masked addresses, folders and mail stay at Fastmail and the other services.
The "start with Windows" entry is always removed.

**Store edition:** uninstall from Windows Settings → Apps. The Store can't ask about your data, so the encrypted folder
`%APPDATA%\DarkCipher\MaskedMail` is kept. Delete it yourself if you want it gone, after an encrypted backup if you
might want it back.

## 15. Troubleshooting

| Problem | What to do |
|---|---|
| "Fastmail refused the API token (401)" | The token was revoked, expired or mistyped. Create a new one with the three scopes. |
| "This token has no Masked Email access" / "cannot send mail" | Create a token with Masked Email, Email and Email submission. |
| "The server answered with a redirect" | The server address in Settings is wrong, or it moved. Use the exact API address; MaskedMail never follows redirects. |
| "Creating a sending identity … failed" | Fastmail refused to send from that address. Send from another address, or add it in Fastmail → Sending identities. |
| Mail isn't filed into a folder | Check the address shows 📁 routed and routing is on, then press ⟳ sweep now. The app must be running (the tray is enough). |
| The viewer or editor is blank | Install the Microsoft Edge WebView2 Runtime (the installer can do it). |
| "Too many wrong attempts — wait …" | Wait. It resets after a correct sign-in. |
| Forgot the master password, with "require at start" on | The data can't be decrypted. Reinstall, choose to delete the encrypted settings, then restore from your `.dcxbackup` (its own password) or enter your keys again. |
| An add-on was disabled: "files changed after install" | Its files were modified. Reinstall it from a trusted package. |
| An add-on "timed out" / "protocol error" | Read its run log. It must use the SDK (API 2, the encrypted bridge). |
