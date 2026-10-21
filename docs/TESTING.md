# Testing DarkCipher MaskedMail

There are two layers, and `build.ps1` runs both before it packages anything.

| Layer | Run it with | What it covers |
|---|---|---|
| **Unit tests** (`tests/DcxMail.Tests`, xUnit) | `dotnet test DarkCipher.MaskedMail.sln -c Release` | All the library logic: store and security, HTML, every provider and the queue, throwaways, add-ons with the real sandbox. 0.3.1: 128 tests |
| **Exe self-test** | `DcxMaskedMail.exe --selftest <report.txt>` | What needs the real app process: every window drawn off-screen (also the Store screenshots), capture guard, Settings search, the editor's script policy, the clipboard retry, the process DLL policy. 0.3.0: 5 checks |

For both layers:
- **No real data, no real account.**
  - The tests use their own temp data folder (`DCX_DATA_DIR`, set by a module initializer before anything reads it).
  - The self-test uses a scratch folder.
  - Neither ever touches your real settings.
  - Fastmail and all twelve other services are in-process fakes (`src/DcxMail.Fakes`).
- **No windows on your screen.** Self-test windows are drawn off-screen into `<report folder>\shots\*.png`. Dialogs
  refuse to open while it runs. The unit tests have no UI at all: the add-on approval seam defaults to "refuse".
- **Real add-on runs.** The add-on tests run real Windows PowerShell add-ons through the real Low-integrity sandbox and
  the encrypted bridge, with the SDK from `samples/addons/sdk`.
- **One at a time.** The store and settings are process-wide, so the tests run sequentially, and each starts from an
  empty store with a new key.

## Build gates (`build.ps1`)

1. **Locked restore.** Package versions must match the committed `packages.lock.json` files.
2. **Solution build, warnings are errors.**
   - .NET analyzers at `latest`, with every security rule (`AnalysisModeSecurity=All`) and the recommended
     reliability rules;
   - a NuGet vulnerability audit of every package, direct and transitive, at any severity.
   - Each suppression is in the code with its reason:
     - TLS 1.2/1.3 is a deliberate floor;
     - HMAC-SHA1 is what RFC 6238 authenticator apps require.
3. **Unit tests.** All must pass. `dist\<version>\tests-<version>.txt` lists each test and the result. The raw log
   isn't shipped, because it holds local paths.
4. **Publish.** It fails if a `.pdb` is in the output.
5. **Exe self-test** on the published exe. `dist\<version>\selftest-<version>.txt` is the report.
6. **MSI:** WiX validates the installer as it builds it (`-wx` turns warnings into errors).
7. **MSIX:** `tools\make-msix.ps1 -Check` (or the real Store package once the identity is filled in) packs with
   Microsoft's `makeappx`, which validates the manifest against the schema: identity, capabilities and the startup task.
8. **Store screenshots:** the build stops if one is smaller than 1366×768. They're the self-test's main-window and
   Settings renders, with demo data only.
9. **Release notes:** the build stops if `docs/release-notes/<version>.md` is missing. The changelog is generated from
   the release notes.

## Unit tests

**Storage and security** (`StoreSecurityTests`)
- encrypt/decrypt round trip · a file is bound to its name · tampering is detected
- settings are saved encrypted (keys not in plain text) and keep case-insensitive folders
- the 0.1.x `key.dpapi` moves into the key ring
- master password on start: no Windows copy of the key, wrong password refused, verify, remove
- TOTP: RFC 6238 test vectors
- 2FA: single-use codes (no replay), ±30 s window, single-use recovery codes, sealed secret
- backups: own password, nothing in plain text, wrong password refused
- 6 weak passwords refused · 3 strong ones accepted
- sign-in throttle: counted across restarts, backs off, resets on success
- the data folder is locked to this Windows user
- security log: sealed lines, appended junk shows, a line removed from the middle is detected (hash chain)
- error log: sealed lines, readable back
- app lock needs a factor; sandbox mode needs a required password

**HTML and parsing** (`HtmlTests`)
- data: images become cid parts · plain-text alternative · cid references resolved for the reader
- the sanitizer removes scripts, handlers, javascript: links, forms and frames, and stops 13 known bypasses (one test
  each), while ordinary mail survives
- the reader page blocks scripts and remote content by default
- address parsing · folder names · routing matches To/Cc/Bcc/Delivered-To
- addy.io send-from format · Fastmail emailPrefix rules and forDomain origin

**Services** (`ProviderTests`)
- Fastmail (fake JMAP):
  - create → folder `Inbox/automation/<address>` → sweep; the automation folder is reused;
  - a folder deleted in Fastmail is forgotten, never a target; disable and delete;
  - quiet sweeps don't re-fetch the folder list;
  - send: plain message, attachment only, inline images (identity, cid, Sent copy);
  - the outgoing queue: split, duplicate, copies, skipped items, failures, encrypted at rest;
  - a token without the Masked Email scope gives a clear error
- SimpleLogin, addy.io and Firefox Relay (fakes): list (pages), create, toggle, delete, reverse alias, auth headers
- one failing service does not hide the others
- redirects are refused and the key never reaches the other host; http:// is refused; a JMAP session naming a
  plain-http API URL is refused

**Throwaways** (`ThrowawayTests`)
- one call makes a random address with its own folder (even with routing off) and an encrypted expiry
- expiry deletes the address, moves its mail to Trash and removes its folder; extend and keep work
- a folder the user renamed is never emptied or removed
- "disable" mode keeps the address and its mail; a failure is kept and retried
- verification codes and confirmation links are picked out; years, prices, footers, unsubscribe links are not
- custom lifetimes parse (45m, 6h, 3d, 90) and silly ones are refused

**The service catalog** (`ServiceCatalogTests`)
- all 12 services are listed once, each with a name, summary, set-up help, https-only links and unique fields
- for each service, what it declares matches what it implements (domain choice, forward choice, own inbox, activity,
  sending): one test per service
- disposable inboxes are opt-in with a caution and a credit; DuckDuckGo's unofficial interface is flagged; only
  configured services are built
- 0.2 settings (SimpleLogin, addy.io, Relay keys) move into the service list and are never written back
- service keys are stored encrypted
- a link a service hands back to another host is refused before any token is sent
- random names are unguessable (200 distinct, 12 characters); wanted names are cleaned
- the services left out are listed with a reason

**Masked-email services** (`MaskedServiceTests`)
- DuckDuckGo: creates with its bearer token, remembers the address (encrypted), "delete" only forgets it without
  calling DuckDuckGo, and refuses odd answers (3 cases)
- SimpleLogin: free suffixes first and verified mailboxes default first; a custom alias uses the chosen suffix and
  mailbox; a random alias is moved only to a non-default mailbox; activity lists forwards and blocks
- addy.io: the chosen domain, recipient and name style are used; an unknown name style is never sent

**Your own domains** (`DomainServiceTests`)
- Forward Email: lists (catch-all skipped), creates with recipients and the chosen "when off" code, turns off, notes,
  deletes, and sends through its API, with the sender name stripped of quotes and line breaks (no header injection);
  it refuses to create without a forward-to address
- Cloudflare: lists exact-address rules only (catch-all skipped), offers only verified destinations, creates
  forwarding rules, turns off and renames by rewriting the whole rule (matchers, actions and priority kept), and
  reports a refusal inside a 200 answer
- ImprovMX: lists, creates a random name, deletes, shows the delivery log; it has no off switch
- Migadu: creates only for a mailbox on the same domain
- mailcow: lists aliases and time-limited ones (not catch-alls), puts the mailbox's own domain first, makes the
  server end a throwaway itself (validity rounded up to hours), turns off and keeps notes on ordinary aliases, reports
  a refusal inside a 200 answer, and refuses an http:// server

**Disposable inboxes** (`DisposableServiceTests`)
- Mail.tm: creates an account with a 32-character random password kept encrypted; reads, marks read
  (merge-patch), deletes messages, saves attachments, signs in again once when the token expires, deletes the
  account; refuses an inbox made on the other server
- TempMail.lol: creates with a prefix, reads by token, keeps read and deleted marks itself, and says when it has ended
- DropMail: lists domains, opens a session on the chosen domain, reads it, follows its expiry, says when it has
  ended; a session id with quotes can't break out of the GraphQL query
- with the app: a disposable throwaway is capped to the service's lifetime, can't be extended, is read at the service
  (with its credit), gets no Fastmail folder and is forgotten when it ends; a DuckDuckGo throwaway is honestly only
  forgotten, and the security log says so

**Add-ons** (`AddonTests`)
- refused: API 1, unsafe paths, unknown permissions, program+theme packages, bad themes
- a theme is sealed on disk and applied; tampering disables it
- a program runs at Low integrity, can't be opened by other processes, can't write to the profile, over the encrypted
  bridge
- an ungranted permission is refused; a declined approval blocks the send; queue.add works with its grant
- a plain-text line ends the run; changed files block it; updates can't gain permissions; downgrades are refused

## Exe self-test checks

- store: opens in the scratch folder, settings saved encrypted, folder locked to this user
- process: DLLs from remote or Low-integrity-labelled files are refused (the policy the real exe applies at start)
- editor: embedded; only its own script may run (fresh nonce per load); no remote images unless allowed
- clipboard: a busy clipboard is retried, and one that stays busy fails quietly (simulated; never touches your
  clipboard)
- renders (14 PNGs):
  - the main window (dark, light, and with a throwaway selected), at 1600×960 so they double as Store screenshots;
  - Settings (Security, Fastmail, masked-email services, your own domains, disposable inboxes, Throwaway addresses), with
    search reaching every security option and the new services;
  - the sign-in window, and that it is hidden from screen capture;
  - the throwaway live inbox, the queue, a new masked address, and the composer.

## Not covered (needs a real account or a person)

- Real Fastmail behaviour:
  - sending from a masked address worked in the first real-account test (0.1.2);
  - folder names that contain `@` and `.`.
- Real accounts on the other twelve services (each was built from its documented API and tested against a fake that
  answers the same way; DuckDuckGo's interface is unofficial).
- MSI install, upgrade and uninstall. These are checked by an admin extract and a self-test of the extracted exe.
- **Installing the MSIX** and running it with package identity: the startup task, the shared data folder, and the
  add-on sandbox from inside a package. Use `tools\make-msix.ps1 -TestSign` for that. Microsoft's certification also
  runs its own checks on submission.
- WebView2 behaviour with real hostile mail: the CSP and sanitizers are unit-tested, the rendered DOM is not.
