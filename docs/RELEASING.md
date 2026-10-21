# Releasing MaskedMail

A release is two commands: `build.ps1` makes it, `release.ps1` publishes it. Everything that is published (the public
repository, the GitHub Release and, later, the website) comes from one file, the privacy-scanned source zip in
`dist\<version>\`, so they can't drift apart.

## Cutting a release

1. Bump `<Version>` in `Directory.Build.props` (the only place it is written) and add
   `docs\release-notes\<version>.md`, starting `# DarkCipher MaskedMail <version> (<date>)`.
2. Build:

   ```powershell
   powershell -ExecutionPolicy Bypass -File build.ps1
   ```

   It builds and tests everything, renders the screenshots and writes `dist\<version>\`. That includes the MSI, the zip,
   `SHA256SUMS.txt` and `DarkCipherMaskedMail-<version>-source.zip`, the public source (see *What is published* below).
3. Look at the new screenshots in `docs\screenshots\`. `build.ps1` lists any image nobody has approved yet with its
   SHA-256. Add each one to `tools\approved-images.txt` once you've seen it.
4. Check what publishing would do, then publish:

   ```powershell
   powershell -ExecutionPolicy Bypass -File release.ps1 -DryRun
   powershell -ExecutionPolicy Bypass -File release.ps1
   ```

## The steps

`release.ps1` runs these in order, and a step never starts before the earlier ones are complete. What happened is
recorded in `dist\<version>\release-state.json`.

| Step | What it does | Public? | Already done when… |
|---|---|---|---|
| **verify** | Every expected file is in `dist\<version>\`. `SHA256SUMS.txt` verifies and covers every file. The exe, the MSI, the source's `Directory.Build.props`, its README badge and the release notes all say the same version. Every unit test passed and the self-test has no failures. | No | (only reads; always runs) |
| **sanitise** | Deletes and recreates `%TEMP%\dcxmail-public-<version>\` from the source zip, then checks every file again: the allowlist, no key or certificate files, every image approved, the privacy scan and gitleaks. | No | (always rebuilt from scratch) |
| **mirror** | Checks the clone `..\MaskedMail-public` has exactly the expected `origin`, is on `main` and is clean. Makes its tree the staged copy: one commit `MaskedMail <version> - sanitised public source` and an annotated tag `v<version>`, as DarkCipher0x with the no-reply address. Pushes both, **never with `--force`**. | Yes | `v<version>` is on GitHub with exactly this content. With different content it stops: a published version is never changed. |
| **release** | The GitHub Release `MaskedMail <version>`: notes from the public copy's release notes, plus the MSI, zip, source zip, `SHA256SUMS.txt`, the test reports and the sample add-ons. GitHub's SHA-256 of every file must equal ours. | Yes | Every file is there with a matching SHA-256. A missing file is uploaded; a different one stops the step. |
| **website** | Off for MaskedMail (`tools\release-config.json`: `"website": false`). | Yes | Records `off` |
| **discord** | Off for MaskedMail (`"discord": false`). | Yes | Records `off` |

## Kill switch

Public steps (mirror, release, website, discord) can be stopped at any time without editing anything:

```powershell
setx DCX_PUBLISH_OFF 1
```

Every new shell then holds them (`$env:DCX_PUBLISH_OFF = '1'` holds them in the current one). The local steps still run
and the record says `held`. To lift it, `setx DCX_PUBLISH_OFF 0`, then run `release.ps1` again. `-NoPublish` holds the
public steps for one run.

## When something fails

Nothing that already succeeded is undone. The failed step is recorded as `failed` with the reason, and the run stops.
Fix the cause and run `release.ps1` again: finished steps recognise their own work and skip it (a release with half its
files uploaded gets only the rest). `-From <step>` carries on from a step, once every earlier step is recorded as
complete for that version.

| Failed step | State it leaves | To resume |
|---|---|---|
| verify | Nothing published | Fix `dist\<version>\` (a new build means a new version) |
| sanitise | Nothing published; the reason is listed per file | Fix the source, build a new version; or approve the images it lists |
| mirror | If the push failed, the commit and tag wait in the clone | Run again: it pushes them |
| release | A release with the files uploaded so far | Run again: it uploads the rest |

## What is published

- **`tools\public-allowlist.txt`** lists every file that may be published, by pattern. **`tools\private-list.txt`**
  lists those that never are: the local term list, the gitleaks binary, signing material. A file in neither stops the
  build until it is classified, so nothing is published, or dropped, without a decision. Build output (`bin`, `obj`,
  `dist`, `publish`) is never considered, and `dist\` never goes into git.
- **Scans**, run by `build.ps1` and again by the sanitise step:
  - local user paths, private e-mail addresses and the Windows user name;
  - API keys and tokens (GitHub, Fastmail and others), Discord webhooks and private keys;
  - the private names in `tools\privacy-terms.local.txt` (git-ignored, never published);
  - **gitleaks 8.30.1** with its own rules plus `tools\gitleaks.toml` (Discord webhook URLs, every GitHub token
    format, Fastmail tokens, `user@IP` logins). `tools\release\get-gitleaks.ps1` installs it once into `tools\bin\`
    after checking the download's SHA-256 against the published one.
- **Images:** every image must be listed by SHA-256 in `tools\approved-images.txt`, which means someone has looked at
  it. Scans can't see pixels.

## Checking a release by hand

- **Repository:** the tag `v<version>` on GitHub has the same files as `dist\<version>\DarkCipherMaskedMail-<version>-source.zip`.
  `release.ps1 -DryRun` checks this: the mirror step reports "already on GitHub with exactly this content".
- **Release:** the release step compares GitHub's SHA-256 of every file with the local one on every run.
- **Downloads:** `Get-FileHash <file> -Algorithm SHA256` must match `SHA256SUMS.txt`.

## Rolling back

A published version is never edited or deleted to "fix" it, because its checksums are already public. Release a new
version instead. If something must come down at once:

1. `setx DCX_PUBLISH_OFF 1`, so nothing else goes out.
2. **GitHub Release:** mark it as a pre-release or delete it on GitHub (Releases → the release → Edit or Delete).
3. **Repository:** the next release's commit replaces the files. Removing a pushed commit needs `git push --force`,
   which is never done without Luke's explicit go.
4. **Website and Discord:** not used for MaskedMail yet.

## Website and Discord

Both are switched off until Luke decides MaskedMail goes on darkciphersystems.com and gets a Discord channel. Then:

- **website** will build its bundle only from the staged public copy, plus installers copied from `dist\<version>\`
  that match the GitHub Release's SHA-256s. It hands the bundle to the Website & Deployments session's deploy
  (production needs Luke's go), and compares every live file byte for byte.
- **discord** will read its webhook from Windows Credential Manager (`DarkCipher/Discord/maskedmail`), never from a file.
  Each post is recorded, so it is never sent twice.
