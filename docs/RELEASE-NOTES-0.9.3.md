# Shotwise 0.9.3 (beta)

Picking a hotkey just got a lot easier. If you already have 0.9.2, Shotwise updates itself — you'll see
**"Shotwise 0.9.3 is ready — Restart now / Later"**.

## New

- **Hotkey suggestions.** When you edit a preset, Shotwise now suggests common keys that are actually free on your
  PC — click one to use it. The suggestions match the preset: **PrintScreen first for All-in-One**, then
  Shift/Ctrl+PrintScreen; Ctrl+Shift+PrintScreen for Image, Alt+Shift+PrintScreen for Video, Ctrl+Alt+PrintScreen for
  Grab Text, then function-key combinations such as Ctrl+Alt+F1. Shotwise deliberately doesn't suggest keys like
  Ctrl+Shift+T (reopens browser tabs) or Ctrl+Alt+letters (AltGr on many keyboards types characters with them), because a
  global hotkey would take them away from every app.
- **"Is this key free?" check.** Under the hotkey box you now see right away whether the key works:
  **Available**, **In use by another app** (with the app that most likely holds it), **Used by Shotwise for …**, or
  **Reserved** (keys Windows keeps for itself, such as Win+L or Alt+PrintScreen). Press **Check** to ask Windows again
  after you changed something in another app.
- **Move a key between hotkeys.** If the key you want belongs to another Shotwise hotkey — for example PrintScreen on
  the global Capture hotkey — choose **Move here** and Shotwise frees it from the other hotkey when you press OK.
- **Check all for conflicts** in **Preferences → Hotkeys** re-checks every hotkey at once, shows which app probably
  holds the busy ones, and offers **Use suggested: …** plus a few free alternatives right under the row.

## Fixed

- A global hotkey you cleared ("None") in Preferences → Hotkeys no longer comes back with its default after
  Shotwise restarts.

## Tip

Windows can't say for sure which app holds a hotkey, so "likely" is an educated guess based on the apps that are
running. If PrintScreen is taken, Shotwise shows how to free it: turn off the other app's PrintScreen shortcut (or quit
it), or switch off **Windows Settings → Accessibility → Keyboard → "Use the Print screen key to open screen capture"**.
You can still save a key that is in use — Shotwise warns you that it won't work until it is freed.
