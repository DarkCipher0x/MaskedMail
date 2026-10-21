# Security policy

## Reporting a vulnerability

Please report security problems **privately** through GitHub: the repository's **Security** tab → **Report a
vulnerability**. Don't open a public issue for anything that could put users or their mail at risk.

Include the version (Settings → About, or the MSI file name), what you did, what happened and what you expected. Leave
out real API tokens, keys, passwords, e-mail addresses and message content: use made-up values and `example.com`
addresses.

## Supported versions

Only the latest release receives fixes. Each release on the Releases page carries SHA-256 checksums
(`SHA256SUMS.txt`), the unit-test and self-test reports, and the source it was built from.

## What is in scope

- The application (`src\`), the installer (`installer\`), the build and packaging scripts (`build.ps1`, `tools\`), and
  the add-on bridge and SDK (`samples\addons\`).
- How the app stores data (the encrypted store, key ring, security log, outgoing queue, backups) and what it sends to
  Fastmail and the other services.

Problems in a third-party service itself (Fastmail, SimpleLogin, addy.io and the rest) belong with that service.

How the app is built to resist attack is described in [docs/SECURITY.md](../docs/SECURITY.md), and the last hardening
review in [docs/SECURITY-AUDIT-0.1.6.md](../docs/SECURITY-AUDIT-0.1.6.md).
