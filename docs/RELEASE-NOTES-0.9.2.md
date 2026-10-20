# Shotwise 0.9.2 (beta)

A small fix release. If you already have 0.9.1, Shotwise updates itself — you'll see **"Shotwise 0.9.2 is ready —
Restart now / Later"**.

## Fixed

- **Setting a hotkey now works with PrintScreen.** Pressing PrintScreen (or another key that is already a global
  hotkey) in the hotkey box used to start a capture instead of recording the key. The hotkey boxes in **Edit preset**
  and **Preferences → Hotkeys** now record any combination you press — including PrintScreen and keys that another
  screenshot app is holding — without triggering a capture.
- **Capture window:** the "changed since saved" row no longer overlaps "More options" or gets cut off; it now shows a
  compact **• Modified · Save**.

## Tip

If another screenshot app (or Windows' "Use the Print screen key to open screen capture" setting) owns PrintScreen,
Shotwise shows **Using fallback** in Preferences → Hotkeys and uses Shift+PrintScreen. You can pick any other key
there, or turn the other app's PrintScreen shortcut off and restart Shotwise to get PrintScreen back.
