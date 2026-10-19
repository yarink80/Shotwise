# Security policy

## Reporting a vulnerability

Please **do not open a public issue** for security problems.

Report them privately through GitHub's private vulnerability reporting:
**[Security → Report a vulnerability](https://github.com/yarink80/Shotwise/security/advisories/new)**.

Include what you can:

- the Shotwise version (Preferences → General, or the installer name) and your Windows version;
- what an attacker can do and what they need (local access, a crafted file, a network position, ...);
- steps or a proof of concept, and any logs from `%LOCALAPPDATA%\Shotwise\logs` that help (remove anything private first).

You should get an acknowledgement within a few days. Fixes ship as a new release through the normal update channel, and the
advisory is published once an update is available. Credit is given unless you prefer otherwise.

## Supported versions

Only the latest release receives security fixes. Installed copies update themselves from GitHub Releases unless automatic
updates were turned off in Preferences → Startup & Updates.

## Scope notes

Areas where reports are especially welcome:

- file parsing: `.shotx`, imported images/PDF/GIF/MP4, stamp/template/theme packs and `shotwise://` links;
- the optional phone-upload server (Mobile Connect, off by default) and its QR pairing;
- credential handling for share destinations (Windows Credential Manager, OAuth with PKCE and loopback redirect);
- the update path (Velopack packages from GitHub Releases) and the installer;
- the Explorer shell extension (thumbnail and property handlers for `.shotx`).

Shotwise ships no telemetry endpoint and no OAuth client secrets. Reports about destinations you configure with your own
accounts are in scope when Shotwise mishandles the credentials or data.
