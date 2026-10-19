# Shotwise 0.9.1 (beta)

Welcome to the **first public release** of Shotwise: free, open-source screen capture, recording and annotation for
Windows. Press PrintScreen, mark up what you captured, and share it. No account, no ads, no telemetry.

## Highlights

- **Capture anything.** Regions, windows, the full screen, scrolling pages, panoramas, menus and the text on screen
  (Grab Text). Timed and repeating captures, presets and global hotkeys.
- **Record your screen** as MP4 with microphone, system audio and webcam, then trim it or turn it into a GIF.
- **Explain it fast.** Arrows, callouts, numbered steps, shapes, blur, 2,600+ stamps, Quick Styles, themes and
  templates. Everything stays editable in `.shotx` files.
- **Private, on-device AI.** Smart Redact hides e-mail addresses, phone numbers, card numbers and faces; Grab Text copies
  text out of any image. Nothing leaves your PC.
- **Library** with full-text search, including the text inside your images.
- **Share** to a file, the clipboard, a printer, e-mail, FTP/SFTP, Office or your own cloud accounts.

## New in 0.9.1: automatic updates

- Shotwise checks for a new version when it starts and once a day, downloads it in the background and asks before
  restarting. Nothing is installed while you record, and you are asked to save unsaved work first.
- When an update is ready you see "Shotwise 0.9.x is ready" in the editor, in a Windows notification and in the tray
  menu. Choose **Restart now**, or **Later**: the update then installs quietly the next time you exit Shotwise.
- Preferences → **Startup & Updates** shows your version, the last check and what's new. You can check now, turn off
  automatic checks, ask before checking, change how many days pass between checks, or join the **Beta** channel.
- No surprise downloads on metered connections (phone hotspots, data plans): Shotwise tells you about the update and
  waits until you choose Download.
- The tray icon now also appears when Windows starts the taskbar after Shotwise, or when Explorer restarts.
- Quick Style previews are clearer: heavy arrows and lines no longer fill their tile, freehand highlighter and blur styles
  show a real preview, and very light styles get a dark backdrop.
- Exports with no folder configured now honour a redirected data folder (`SHOTWISE_DATA_ROOT`), so isolated test runs
  never write into your real Documents folder.

## Install

- **New here:** download **Shotwise-Setup.exe** and run it. It installs for your user only in a few seconds; no
  administrator rights needed. See the [install guide](https://github.com/yarink80/Shotwise/blob/main/docs/INSTALL.md).
- **You have the 0.9.0 preview:** run the 0.9.1 **Shotwise-Setup.exe** once. It installs over 0.9.0 and keeps your
  captures and settings. 0.9.0 cannot update itself; from 0.9.1 on, updates arrive automatically.

## Where updates come from

Updates are downloaded from the Shotwise releases on GitHub (github.com/yarink80/Shotwise) over HTTPS, and each download
is checked against the checksum published with the release before it is installed. Nothing else is sent: no usage
data, no account.

## Windows may warn you the first time

Shotwise is not yet signed with a paid certificate, so Windows SmartScreen may say "Windows protected your PC".
Click **More info**, then **Run anyway**.

## Known limitations

- English only for now (translations are welcome).
- Cloud destinations (OneDrive, Google Drive, Dropbox and others) need your own app registration; see "Setting up cloud
  share destinations" in the README. Saving, copying, printing and e-mail work out of the box.
- Windows 10 version 2004 or newer, 64-bit. Windows 11 is recommended.
