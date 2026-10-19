# Shotwise 0.9.0 (beta)

Thanks for trying Shotwise! This is an early test version for family and friends.

## What it does

Shotwise takes pictures and videos of your screen, lets you mark them up, and helps you share them.

- **Capture** a region, a window, the whole screen, a scrolling page, or just the text on screen (Grab Text).
- **Record** your screen as an MP4 video, with your microphone and computer sound if you want.
- **Mark up** captures with arrows, boxes, text, callouts, stamps, numbered steps, blur and highlights.
- **Find** older captures again in the library. Everything is saved automatically.
- **Share** by saving a file, copying to the clipboard, printing or attaching to an email.

## How to start

1. Run **Shotwise-Setup.exe**. It installs in a few seconds. You do not need to be an administrator.
2. Shotwise opens by itself. You will see the small **Capture** window and the editor with a short welcome.
3. Press **PrintScreen** to capture. If another app already uses PrintScreen (on Windows 11 the Snipping Tool usually
   does), Shotwise uses **Shift+PrintScreen** instead and tells you once. The Capture button always shows the key that works.
4. Drag over the part of the screen you want, then let go. The picture opens in the editor.
5. Later, start Shotwise from the **Start menu** or the desktop icon. Shotwise also waits in the system tray
   (bottom-right corner, near the clock).

Other handy keys: **Ctrl+Shift+PrintScreen** (region), **Alt+Shift+PrintScreen** (video), **Ctrl+Alt+PrintScreen**
(grab text). In the editor, press **Ctrl+/** to see all keyboard shortcuts.

## Windows may warn you the first time

This test version is not signed with a paid certificate, so Windows SmartScreen may say
"Windows protected your PC". Click **More info**, then **Run anyway**. Your antivirus may also scan the file first.
Only do this if you got the file directly from the person who sent it to you.

## Your privacy

- Shotwise sends **no usage data** (telemetry). It has no way to send any.
- Your captures and recordings **stay on your PC** unless you share them yourself.
- Shotwise does not check for updates on the internet in this version. Optional extras (face detection and
  better object removal) are downloaded only if you ask for them in Preferences → Models.

## Known limitations

- English only for now.
- Cloud sharing (OneDrive, Google Drive, Dropbox, Slack and others) needs developer accounts that are not set up in
  this version. Save to a file, copy, print or email instead.
- No automatic updates yet. To update, install the next version over this one. Your captures and settings are kept.
- Windows only (Windows 10 version 2004 or newer, 64-bit). Windows 11 is recommended.
- Some advanced capture types (scrolling, panoramic, menus) may not work in every app yet.
- Explorer previews of `.shotx` files work, but searching for Shotwise details in Windows Search does not.

## How to uninstall

Open **Settings → Apps → Installed apps**, find **Shotwise**, and choose **Uninstall**. It is quick and quiet. If
Shotwise is still open, it is closed without asking, so exit it from the tray icon first if you are in the middle of
something.

Uninstalling removes the program, its Start menu and desktop icons, and its file types. It **keeps your captures and
settings**, in case you reinstall. To remove those too, delete these folders yourself:

- `Documents\Shotwise` (your library of captures)
- `%APPDATA%\Shotwise` (settings and presets)
- `%LOCALAPPDATA%\Shotwise` (library index, thumbnails, logs and downloaded extras)
