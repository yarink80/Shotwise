# Linux roadmap

Status: planned, not started in this repository. This note summarises the direction so contributors know where the
Windows code base is heading. Details may change.

## Goal

A full Shotwise for Linux desktops (Wayland first, X11 where practical): capture overlay, editor, library, sharing,
recording, tray and global hotkeys, reading and writing the same `.shotx` documents as the Windows app.

## UI technology

- **Linux UI: [Avalonia](https://avaloniaui.net/)** (MIT). The editor canvas keeps using SkiaSharp and the existing
  `SkiaDocumentRenderer`, hosted in Avalonia through a custom draw operation. A prototype rendered real `.shotx`
  documents through the unchanged renderer on Linux; output matched the Windows build except for font fallback
  (Segoe UI is not available on Linux).
- **Windows stays on WPF** for now. Whether Windows later moves to the same UI stack is decided once the Linux editor
  covers the specification without regressions in Windows-only features (per-monitor DPI overlays, Windows.Graphics.Capture,
  UI Automation element capture).

## Code structure

- `Shotwise.Core`, `Shotwise.Editor` and the engine contracts are already UI-free and are shared as they are.
- View-models move out of the WPF projects into a UI-neutral library, so the WPF and Avalonia front ends can share them.
- Platform services get Linux implementations behind the existing Core interfaces: screen capture and global shortcuts
  through the XDG desktop portals (PipeWire for video), notifications through the freedesktop notification service, OCR
  through an optional engine such as Tesseract.

## Not decided yet

- Packaging format (Flatpak, AppImage or distribution packages).
- Which Windows-only features (Office destinations, shell extension, virtual printer) get Linux equivalents.

Ideas and help are welcome in [Issues](https://github.com/yarink80/Shotwise/issues).
