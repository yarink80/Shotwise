# Updates

Installed copies of Shotwise update themselves from the **GitHub Releases** of
[`yarink80/Shotwise`](https://github.com/yarink80/Shotwise/releases). The installer and the updater are
[Velopack](https://velopack.io) (MIT). This page covers what users see, how to publish a release, and the trust model.

## For users

| When | What happens |
|---|---|
| Shotwise starts | 30 s later it checks GitHub, if a check is due. |
| Every hour while running | It checks again once **Days between checks** (default **1**) has passed since the last check. |
| A newer version exists | It downloads in the background: a delta package (usually a few MB) when one exists, otherwise the full package (~160 MB). On a **metered** connection (phone hotspot, data plan) it does not download and shows "available" with a **Download** button instead. |
| The download is ready | The editor shows an InfoBar, "**Shotwise X.Y.Z is ready — Restart now / Later**". You also get a Windows notification (when notifications are on) and a tray menu item, "Restart to update to X.Y.Z". |
| **Restart now** | Refused while a capture or recording runs. Otherwise Shotwise saves autosaved documents, asks about every other unsaved document (Cancel keeps it open), exits cleanly and restarts on the new version. You then get the notification "Shotwise was updated". |
| **Later** | Nothing interrupts you. The update installs silently after you next exit Shotwise. |

**Preferences → Startup & Updates** has these settings:

- your version and channel, the last check, the live status and a "What's new" excerpt of the release notes;
- **Check for updates**, **Download** and **Restart now** buttons. **Check for updates** is also in the editor's menu → Help;
- **Update channel**: **Stable** gets normal releases. **Beta** also gets GitHub *pre-releases* such as `0.9.2-beta.1`. Switching
  from Beta back to Stable never downgrades. You stay on the beta until a newer stable release exists;
- **Automatically check for updates**, **Download updates in the background (not on metered connections)**, **Ask before
  checking for updates** (a notification with **Check now** appears instead of a silent check) and **Days between checks** (1–365).

Updates are off in these cases:

- development builds, `--smoke` runs, and any run with `SHOTWISE_DATA_ROOT` set (test isolation);
- the policy `allowUpdates: false` / `HKLM\Software\Policies\Shotwise\AllowUpdates = 0` (Preferences shows "turned off by your
  organization").

The portable zip uses the same Velopack layout, so it updates in place as well.

If you are offline, the check fails quietly. The status line says so and the next hourly check retries.

**0.9.0 has no updater.** Anyone on 0.9.0 installs 0.9.1 once by hand, by running `Shotwise-Setup.exe` over the existing
install. Later versions arrive automatically.

## For the maintainer: publishing a release

The version lives in **one place**: `<Version>` in `Directory.Build.props`. Release notes are `docs/RELEASE-NOTES-<version>.md`.
Setup shows them, the app's update prompt and Preferences show them, and they become the GitHub release body.

### Option A: from this PC

```powershell
# 1. write docs/RELEASE-NOTES-0.9.2.md
pwsh tools/release.ps1 -Version 0.9.2            # dry run: bump Directory.Build.props, build + all tests, package -> dist/0.9.2
# 2. commit + push the bump and the notes (vpk tags the default branch head on GitHub)
pwsh tools/release.ps1 -Version 0.9.2 -Publish -SkipTests
```

`tools/release.ps1` runs these steps:

1. Sets `<Version>`.
2. Requires the release notes.
3. Runs `tools/build.ps1 -Test`.
4. Runs `tools/package.ps1 -DownloadPrevious`. That runs `vpk download github` first, so `vpk pack` builds a **delta** against the
   latest published release, then publishes, prunes and packs into `artifacts/package/<v>/vpk` and `dist/<v>`.
5. With `-Publish` only, runs `vpk upload github --repoUrl https://github.com/yarink80/Shotwise --publish --releaseName "Shotwise <v>"
   --tag v<v> --token <token>`. The token comes from `-Token`, `$env:GITHUB_TOKEN` or `gh auth token`.

For a beta, run `-Version 0.9.2-beta.1 -Channel beta`. That makes a GitHub pre-release (`--pre`), which only Beta-channel
copies install.

Other options:

- `-SignParams '/a /fd sha256 /tr http://timestamp.digicert.com /td sha256'` or `-AzureTrustedSignFile metadata.json` signs
  everything;
- `-PreviousReleaseDir dist\0.9.1` builds the delta against a local release instead of downloading it.

### Option B: push a tag (CI)

```powershell
pwsh tools/release.ps1 -Version 0.9.2      # bump + notes + local check
git commit -am "Release 0.9.2"; git tag v0.9.2; git push --follow-tags
```

`.github/workflows/release.yml` runs on `v*` tags, or by hand with a version input and an optional "draft" flag. It runs
on `windows-latest` and does these steps:

1. Checks that the tag equals `<Version>` and that the release notes exist.
2. Sets up .NET 8 and installs `vpk` 1.2.161.
3. Runs `tools/build.ps1 -Test`, which builds the native shell extension with the runner's Visual Studio C++ tools and runs
   every test (the opt-in hardware and network tests stay skipped).
4. Runs `tools/package.ps1 -DownloadPrevious`, which does `vpk download github` and then `vpk pack`.
5. Runs `vpk upload github --merge --publish` with `GITHUB_TOKEN`, which needs `permissions: contents: write`. A `-beta.N`
   tag becomes a pre-release.

It also keeps `dist/<v>` as a workflow artifact for 30 days. `.github/workflows/ci.yml` builds and tests every push and
pull request.

**Optional code signing in CI.** Add the repository secrets `SIGN_PFX_BASE64` (base64 of a code-signing `.pfx`) and
`SIGN_PFX_PASSWORD`, plus `SIGN_TIMESTAMP_URL` if you want one. When they are absent, signing is skipped and the release is
unsigned. No secret is stored in the repository.

### What a GitHub release contains

| Asset | Purpose |
|---|---|
| `Shotwise.App-win-Setup.exe` | Installer for new users. Velopack names it on GitHub; `dist/<v>/Shotwise-Setup.exe` is the same file. |
| `Shotwise.App-win-Portable.zip` | Portable copy. |
| `Shotwise.App-<v>-full.nupkg` | Full update package. |
| `Shotwise.App-<v>-delta.nupkg` | Delta from the previous release, when one existed. |
| `releases.win.json`, `RELEASES`, `assets.win.json` | The feed the updater reads, with version, size, SHA-1/SHA-256 and release notes per package. |

### Testing an update locally (no GitHub)

```powershell
pwsh tools/package.ps1 -Version 0.9.1-test.1 -ReleaseNotes notes1.md -OutputRoot artifacts\update-test -NoPortable
pwsh tools/package.ps1 -Version 0.9.1-test.2 -ReleaseNotes notes2.md -OutputRoot artifacts\update-test -NoPortable `
     -PreviousReleaseDir artifacts\update-test\0.9.1-test.1
artifacts\update-test\0.9.1-test.1\Shotwise-Setup.exe --silent
$env:SHOTWISE_UPDATE_SOURCE = "$PWD\artifacts\update-test\0.9.1-test.2"   # absolute local folder: replaces GitHub
& "$env:LOCALAPPDATA\Shotwise.App\current\Shotwise.exe"
```

The scheduled check runs 30 s after startup (clear `updates.lastCheck` in `%APPDATA%\Shotwise\settings.json` to make it due
again), or use Help → Check for Updates.

`SHOTWISE_UPDATE_SOURCE` is an explicit test opt-in. It is honoured only as an absolute, existing **local** folder (no
UNC, no URL), it is logged as a warning, and Preferences shows it as the update source. Reinstall a normal build to go back:
`dist\<v>\Shotwise-Setup.exe --silent` over a newer test build works.

## Trust model

- **Feed and transport.** Installed copies read `releases.win.json` and the packages of the releases of
  `github.com/yarink80/Shotwise` over HTTPS, anonymously. GitHub's API allows 60 requests per hour per IP, and a check uses
  about two. Whoever can publish a release in that repository can ship code to every installed copy. **Protect the GitHub
  account** with 2FA or a passkey, keep the repository's write access small, and prefer tag-triggered CI releases with
  `GITHUB_TOKEN`, which is scoped to the repository and expires with the job, over long-lived personal tokens.
- **Integrity.** Velopack checks each downloaded package's SHA-256 (and SHA-1) against `releases.win.json` of the same
  release before it can be applied, and applies it atomically: `current\` is swapped, and a failed apply rolls back. This
  protects against corrupted or truncated downloads. It does **not** protect against a compromised GitHub account, because the
  hashes come from the same place as the packages.
- **Authenticity: code signing (recommended).** Releases are currently **unsigned**: SmartScreen warns on first install,
  and nothing proves who built a package. Signing with an OV/EV certificate or Azure Trusted Signing (`-SignParams` /
  `-AzureTrustedSignFile`, or the CI secrets above) signs every binary, `Update.exe` and Setup. That gives
  Explorer and SmartScreen a verifiable publisher, and later tooling can pin it.
- **Not used any more.** The earlier signed-JSON-manifest updater (RSA-PSS manifest key and placeholder) was removed: it had
  no key, no feed and no installer path. Velopack's feed replaces it. The Core contract `IUpdateService` stays,
  reshaped for check → download → restart/exit-apply.
- **Privacy.** A check sends nothing but an anonymous HTTPS request to the GitHub API and release downloads. There is no ID,
  no telemetry and no account.

## Code map

| Piece | Where |
|---|---|
| Contract (`IUpdateService`, `UpdateStatus`, `IUpdateRestartHost`, `ReleaseNotes`) | `src/Shotwise.Core/Platform/PlatformContracts.cs`, `ReleaseNotes.cs` |
| Settings (`UpdateSettings`, `UpdateChannel`; schema 2 migrates the old weekly default to daily) | `src/Shotwise.Core/Settings/AppSettings.cs` |
| Rules: schedule, metered, guards, channel mapping, local source | `src/Shotwise.Platform/UpdatePolicy.cs` |
| Orchestration: scheduler, states, policy, offline, metered pause, restart/exit apply | `src/Shotwise.Platform/UpdateService.cs` |
| Velopack engine: `UpdateManager` + `GithubSource` / `SimpleFileSource` | `src/Shotwise.App/Hosting/VelopackUpdateEngine.cs` |
| Toasts, tray "Restart to update", restart host | `src/Shotwise.App/Hosting/UpdateNotifier.cs`, `Tray/TrayIconManager.cs` |
| Editor InfoBar, Help → Check for Updates | `src/Shotwise.UI.Editor/Infrastructure/UpdateBar.cs`, `EditorWindow.Commands.cs` |
| Preferences → Startup & Updates | `src/Shotwise.UI.Capture/Preferences/UpdateStatusPanel.cs`, `CapturePreferencePages.cs` |
| Install, update and restart hooks | `src/Shotwise.App/Hosting/InstallHooks.cs` |
| Tests | `tests/Shotwise.Platform.Tests/UpdateServiceTests.cs` |
| Tooling | `tools/release.ps1`, `tools/package.ps1`, `.github/workflows/release.yml`, `.github/workflows/ci.yml` |
