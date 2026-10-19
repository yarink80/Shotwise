# Installing Shotwise

Shotwise is a free, open-source screen capture app for Windows: take a picture or video of your screen, mark it up,
and share it. This guide walks you through installing it for the first time.

**You need:** Windows 10 (version 2004 or newer) or Windows 11, 64-bit. About 650 MB of free space (the app uses
about 580 MB once installed). No administrator rights, no .NET or other extra downloads.

## 1. Download

Open the [latest release](https://github.com/yarink80/Shotwise/releases/latest) and download **Shotwise-Setup.exe**
(about 165 MB), for example into your Downloads folder. A portable zip and `SHA256SUMS.txt` are attached to the release too.

To check that the file arrived intact, you can compare its fingerprint with the one in `SHA256SUMS.txt`. Open PowerShell
in the download folder and run:

```powershell
Get-FileHash .\Shotwise-Setup.exe -Algorithm SHA256
```

## 2. Run the installer (and the Windows warning)

Double-click **Shotwise-Setup.exe**.

Shotwise is not yet signed with a paid code-signing certificate, so Windows SmartScreen will probably show
**"Windows protected your PC"**. That is expected for a new app from a small publisher:

1. Click **More info**.
2. Check that the app name is `Shotwise-Setup.exe`, then click **Run anyway**.

Your browser may also say the file "isn't commonly downloaded". Choose **Keep**. Only do this for a file you downloaded
from the official [Shotwise releases page](https://github.com/yarink80/Shotwise/releases).

The installer runs for a few seconds with a small Shotwise splash screen and asks no questions. It installs only for
your Windows user account, in `%LOCALAPPDATA%\Shotwise.App`.

## 3. First start

Shotwise starts by itself after installing. You will see:

- the small **Capture** window, with the big round Capture button;
- the editor, with a short welcome page;
- a Shotwise icon in the system tray (bottom-right, near the clock; it may be hidden under the `^` arrow).

Later, open Shotwise from the **Start menu** or the **Shotwise** icon on your desktop.

## 4. Taking a capture

Press **PrintScreen**, or click the Capture button. Drag over the area you want and let go. The capture opens in the
editor, and it is saved to your library automatically.

If another app already uses PrintScreen (on Windows 11, the Snipping Tool usually does), Shotwise uses
**Shift+PrintScreen** instead and shows a short message once. The Capture button always shows the key that works.

| Keys | What it does |
|---|---|
| PrintScreen (or Shift+PrintScreen) | Capture with the selected preset |
| Ctrl+Shift+PrintScreen | Capture a region |
| Alt+Shift+PrintScreen | Record a video |
| Ctrl+Alt+PrintScreen | Grab the text on screen |
| Ctrl+Shift+F9 / Ctrl+Shift+F10 | Pause or resume / stop a recording |
| Ctrl+/ (in the editor) | Show all keyboard shortcuts |

You can change every key in **Preferences → Hotkeys**.

## 5. Where your files are

| What | Where |
|---|---|
| Your captures and recordings (library) | `Documents\Shotwise\Library` |
| Settings, presets, themes | `%APPDATA%\Shotwise` |
| Library index, thumbnails, logs, optional downloaded extras | `%LOCALAPPDATA%\Shotwise` |
| The program itself | `%LOCALAPPDATA%\Shotwise.App` |

Type a path such as `%APPDATA%\Shotwise` into the File Explorer address bar to open it.

**Privacy:** Shotwise sends no usage data. It checks GitHub Releases for a newer version once a day (you can turn this
off in Preferences → Startup & Updates). Your captures stay on your PC unless you share them yourself. The optional
extras in Preferences → Models (face detection, better object removal) are downloaded only if you click to get them.

## 6. Updating

**Shotwise updates itself automatically from [GitHub Releases](https://github.com/yarink80/Shotwise/releases), starting
with version 0.9.1.** When it starts, and once a day after that, it looks for a newer version, downloads it in the background (not on a metered connection such as a phone hotspot) and then shows
"Shotwise X is ready" in the editor, in a Windows notification and in the tray menu:

- **Restart now** saves your work first (it asks about anything unsaved) and restarts Shotwise on the new version. It
  waits if you are in the middle of a recording.
- **Later** keeps working; the update installs quietly the next time you exit Shotwise.

Preferences → **Startup & Updates** shows your version and the last check, has a **Check for updates** button (also in
the editor's menu → Help) and lets you turn automatic checks off, ask before checking, change the number of days
between checks, or join the **Beta** channel for early versions. Your captures and settings are always kept.

You can also run a newer **Shotwise-Setup.exe** over the installed version at any time; it keeps everything.

**Version 0.9.0 cannot update itself.** If you have the 0.9.0 preview, close Shotwise (right-click the tray icon → Exit)
and run the 0.9.1 (or newer) **Shotwise-Setup.exe** once; it installs over 0.9.0 and keeps everything. From then on,
updates arrive on their own.

## 7. Uninstalling

Open **Settings → Apps → Installed apps** (Windows 10: **Apps & features**), find **Shotwise**, click **⋯** →
**Uninstall**. It removes Shotwise quietly in a few seconds. If Shotwise is still running, the uninstaller closes it
without asking, so first finish any capture you are editing (or right-click the tray icon → **Exit**).

Uninstalling removes the program, its Start menu and desktop icons, the `.shotx` file type, the "Edit in Shotwise"
right-click entry and the Explorer previews. It **keeps your captures and settings** so nothing is lost by accident.
To delete them too, remove these folders yourself after uninstalling:

- `Documents\Shotwise`
- `%APPDATA%\Shotwise`
- `%LOCALAPPDATA%\Shotwise`

## If something goes wrong

- **Nothing happens when I press PrintScreen:** look at the Capture button; it shows the key Shotwise could register.
  Another screenshot tool may be using the key; change it in Preferences → Hotkeys.
- **The installer is blocked completely** (no "Run anyway" button): your PC may only allow apps from the Microsoft
  Store, or your antivirus quarantined the file. Check Windows Security, or use the portable zip from the release.
- **Something crashed:** please [open an issue](https://github.com/yarink80/Shotwise/issues/new/choose) and attach the
  newest log from `%LOCALAPPDATA%\Shotwise\logs` (check it for anything private first).
