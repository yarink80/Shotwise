# Shotwise — manual smoke test checklist

Derived from `docs/SPEC.md`. Run on Windows 11 (and once on Windows 10 2004+) with two monitors at different
scaling (e.g. 100 % + 150 %), one of them HDR if available. Use an isolated profile:
`pwsh tools/run.ps1 -Isolated` (sets `SHOTWISE_DATA_ROOT`). Tick each item; record failures with the SPEC ID.

Legend: **[P1]** = core path that must pass before every release (capture → edit → save → library → share File/Clipboard).
**[auto]** = covered by the automated smoke harness below (still worth an occasional manual look).

## Automated smoke harness

The app has a hidden, opt-in mode that drives the composed application without user input and writes a report:

```powershell
$env:SHOTWISE_DATA_ROOT = "$env:TEMP\shotwise-smoke"      # ALWAYS isolate: the run creates library items
& src\Shotwise.App\bin\x64\Debug\net8.0-windows10.0.22621.0\win-x64\Shotwise.exe --smoke `
    --smoke-out=$env:TEMP\shotwise-smoke-out --smoke-screenshots=$env:TEMP\shotwise-smoke-shots   # optional: --smoke-no-video
```

Exit code 0 = all steps passed, 2 = failures (see `smoke-results.md` / `.json` in the output folder; the log is in
`%SHOTWISE_DATA_ROOT%\Local\logs`). Steps: DI seams resolve · library initialised · tray icon · default hotkeys register
(PrintScreen held by another app is reported, not failed; ScreenDraw/webcam keys only while recording) · open a PNG via the
command line · editor renders an annotated document · `.shotx` save/reload · library import + search · PNG/PDF export and a
File share with per-binding options · `--capture=<preset>` fixed-region capture (exactly one library item = single autosave
owner, background OCR job) · 3 s fixed-region recording (moved into the library) · video view · GIF from the recording ·
library view · capture window · Preferences / Create GIF / Share Accounts dialogs · selection overlay (cancelled) · no
duplicate library items. Screenshots are taken from our own window handles (PrintWindow, falling back to rendering the WPF
visual tree), so they work in sessions where desktop capture only shows the wallpaper. Smoke mode marks the one-time
PrintScreen prompt as answered and starts without the capture window/widget.

## 0. Startup & platform (PLT)
- [ ] [P1] App starts in < 3 s; tray icon appears; placeholder/editor window shown (PLT-01, CAP-18).
- [ ] [P1] Starting a second instance does not open a second process; arguments are forwarded (PLT-01).
- [ ] `Shotwise.exe --background` starts tray-only; `--capture` starts a capture; `--capture="Window"` uses that preset; `Shotwise.exe file.png` opens the file (PLT-01).
- [ ] Tray menu: Capture, Open Editor, Open Capture Window, Show Capture Widget (toggle persists), Preferences, Exit (CAP-18).
- [ ] Theme: switch Light / Dark / System in Preferences; System follows Windows changes live; high-contrast mode is legible (PLT-02, PLT-09).
- [ ] Logs written to `%LOCALAPPDATA%\Shotwise\logs\shotwise-yyyyMMdd.log`; no errors on a clean start (PLT-10).
- [ ] Run at Windows start toggle adds/removes the HKCU Run entry (PLT-01).
- [ ] Toasts: capture saved / share completed with "Copy link" / "Open" actions; notification preference All/Help/None honoured (PLT-03).
- [ ] Updater disabled when manifest URL is empty; with a test manifest it detects, verifies signature and refuses a tampered package (PLT-04).
- [ ] Mobile Connect: enable sharing, scan QR, upload a photo from a phone on the same network; file appears in tray/library; expired token is rejected (PLT-05).
- [ ] `.shotx` association, Explorer thumbnail + properties (app/window/URL/OCR), "Edit in Shotwise" verb, `shotwise://` URI import (PLT-06).
- [ ] Telemetry off by default; no network traffic at idle (PLT-07).
- [ ] Policy keys under `HKLM\Software\Policies\Shotwise` disable OCR / outputs / mic / system audio / mobile / FTP / updates (PLT-08).
- [ ] Every window is fully keyboard-operable; Narrator announces controls (PLT-09).

## 1. Capture (CAP)
- [ ] [P1] PrintScreen (default) starts All-in-One; crosshair appears in < 300 ms on all monitors (CAP-01, CAP-19).
- [ ] If Windows' PrintScreen → Snipping setting is on, a conflict prompt appears (CAP-16).
- [ ] Overlay: frozen dimmed snapshot, magnifier (toggle, avoids edges), W×H readout, window/element highlight (skips cloaked & own windows), arrow-key 1 px nudge, Shift/Ctrl modifiers, aspect locks 1:1 / 4:3 / 16:9, guides, resize/move before commit, Esc cancels, F1 help; selections < 8×8 rejected (CAP-04).
- [ ] [P1] Region, Window, Fullscreen (one / all monitors), Fixed Region (W×H, X/Y), Freehand (transparent outside), Object (UIA element), Multiple Area, Menu (cascaded, optional menu bar), Clipboard, Listen to Clipboard, File (CAP-03).
- [ ] All-in-One mini toolbar: Image / Video / Scrolling / Panoramic / Cancel (CAP-01).
- [ ] Scrolling capture of a long web page and a long list (vertical, horizontal, both); sticky header removed; "Require Start button" pref; failure dialog on a non-scrollable window (CAP-05).
- [ ] Panoramic capture with live preview HUD; Start/Stop/Cancel; one stitched image (CAP-06).
- [ ] Grab Text capture opens the OCR result dialog (CAP-07).
- [ ] Step capture: clicks across apps produce numbered steps; HUD count/stop/resume/restart/cancel; result opens as step guide (CAP-08).
- [ ] Delay (5 s, custom) with countdown; interval repeat; scheduled capture (CAP-09).
- [ ] Cursor included in image captures at the right hotspot (CAP-10).
- [ ] HDR monitor capture with "Support HDR" on looks correct (not washed out) (CAP-11).
- [ ] Captures on the 150 % monitor are physical-pixel sharp (CAP-12).
- [ ] Preset effects stack is applied automatically (e.g. border + shadow) (CAP-13).
- [ ] Outputs: copy to clipboard, preview in editor, open in program, share to destination (CAP-14).
- [ ] Presets: 8 built-ins present; create / save current / rename / duplicate / delete / reorder (drag) / groups; export & re-import `.shotpresets`; per-preset hotkey; "Reset current settings" (CAP-15).
- [ ] [auto] Global hotkeys: capture, repeat last, video start/pause/resume, video stop, ScreenDraw (default **Ctrl+Shift+F8**), show/hide; duplicate/unsupported/in-use detection; "Allow override"; Restore Defaults (CAP-16). ScreenDraw and webcam-fullscreen are registered **only while recording** (so Ctrl+Shift+F stays free in other apps and in the editor).
- [ ] Capture Widget docks to an edge, auto-collapses, Image/Video/preset picker work (CAP-17).
- [ ] "Hide app during capture" removes Shotwise windows from captures (CAP-19).
- [ ] Printer capture: install "Shotwise Printer", print from Notepad → pages open in editor; editor Print refuses that printer (CAP-20).

## 2. Video recording (REC)
- [ ] [P1] Record a region to MP4 (H.264 + AAC) with mic + system audio; plays in the editor and Windows player (REC-01, REC-04).
- [ ] Window and fullscreen recording; hardware encoding toggle (REC-01, REC-02).
- [ ] 5/10/15/30/60 fps; "Scale to 1080p" (REC-03).
- [ ] Mic device picker, mute/unmute, VU meter, muted-mic alert; system audio toggle; A/V in sync after pause/resume (REC-04).
- [ ] Remove background noise toggle audibly reduces fan noise (REC-05).
- [ ] Webcam PiP shapes, 4 corners, Ctrl+Shift+F swaps PiP↔fullscreen during a recording (REC-06).
- [ ] Editor "Record video from images" with microphone narration: audio is present in the MP4 (REC-12, via the recorder).
- [ ] HUD: Ready window, countdown, Record/Pause/Resume/Stop/Restart/Cancel, elapsed time, meters; minimized HUD on single-monitor fullscreen (REC-07).
- [ ] ScreenDraw: A arrow, S square, 1 step, fade after N s, enter/exit hotkey; drawings appear in the video (REC-08).
- [ ] Cursor highlight and click ripple rendered into frames (REC-09).
- [ ] Low-disk warning and stop; unplugged mic mid-recording shows a warning (REC-10).
- [ ] GIF recording converts via the GIF pipeline (REC-11).
- [ ] Video from Images: narrate canvas region with mic + webcam, pause/resume → MP4 (REC-12).

## 3. Editor (EDT) and tools
- [ ] [P1] Capture opens in the editor; annotations stay editable after save/reopen of `.shotx` (EDT-01).
- [ ] [P1] Undo/redo (Ctrl+Z/Ctrl+Y) for every operation incl. raster ops; Undo All; Undo Stack window jumps (EDT-02).
- [ ] Selection: click, marquee, Ctrl/Shift multi-select, resize/rotate handles, Ctrl+A, Ctrl+D, Ctrl+Shift+I, Delete, Ctrl+X/C/V, Ctrl+Shift+C, Duplicate, Ctrl+Shift+G/U, Ctrl+Shift+F/B, front/back, lock, align/distribute, Ctrl+; snapping (EDT-03).
- [ ] Canvas: resize (px/%/in/cm, 9 anchors), canvas colour, corner radius, canvas shadow, auto-expand when dragging beyond edge, transparent canvas (EDT-04).
- [ ] View: Ctrl++ / Ctrl+- / Ctrl+0 / fit / slider, Ctrl+G pixel grid, space-drag and middle-drag pan, workspace colour (EDT-05).
- [ ] Tools (EDT-06) — each creates, edits, restyles and undoes correctly:
  - [ ] Arrow (caps none/arrow/open/triangle/round/square/diamond/T-bar, independent sizes, Bezier) · [ ] Line (double, Bezier)
  - [ ] Shape (rect/rounded/ellipse/polygon, fill none, Shift constrain) · [ ] Callout (≥16 presets, draggable tails, text)
  - [ ] Text (rich runs, outline, alignment, padding, auto-delete empty, spell check) · [ ] Stamp (categories, search, favourites, import pack)
  - [ ] Step (Number/A-Z/a-z, restart, edit value, pointer styles, counter persisted in file) · [ ] Pen (nibs, smoothing, antialias)
  - [ ] Highlighter (rect/freehand, multiply) · [ ] Fill (tolerance, transparent, eyedropper) · [ ] Eraser
  - [ ] Magnify (lens shapes, scale, connector) · [ ] Blur (blur/pixelate/solid) + Smart Redact entry
  - [ ] Crop (handles, aspect, apply/cancel/reset; "Delete cropped content") · [ ] Cut Out (H/V, edge None/Fade/Tear/Wave/Saw/Sharktooth)
  - [ ] Selection (rect/ellipse/polygon/freehand; move/copy/cut/delete; transparent vs auto-fill) · [ ] Magic Wand (tolerance, contiguous/global)
  - [ ] Move / Smart Move (detect, move/resize/delete with fill; "no objects found") · [ ] Simplify (manual + Auto Simplify, palettes, show original)
  - [ ] Grab Text / Edit Text · [ ] Cursor objects · [ ] Favorites tool
- [ ] Quick Styles per tool; Themes create/import/export `.shottheme`/restore defaults; Favorites star (EDT-07).
- [ ] Colour picker: RGB/HEX incl. alpha, named colours, screen eyedropper, My Colors (EDT-08).
- [ ] Shadow editor + opacity + outline on every object (EDT-09).
- [ ] Text editing features incl. floating format bar and spell-check language (EDT-10).
- [ ] Tray strip: recent items (max pref), pin Ctrl+Shift+P, rename F2, close Ctrl+F4, delete Ctrl+Del, drag reorder, drag out as file, drag-combine (EDT-11).
- [ ] New Image Ctrl+N, New from Clipboard Ctrl+Shift+N, Open Ctrl+O (images, PDF pages, GIF/MP4, `.shotx`, `.snagx` import), Open Recent, Duplicate Ctrl+Shift+D (EDT-12).
- [ ] Combine Images Ctrl+Alt+C with layouts, caption, swap/zoom per slot (EDT-13).
- [ ] Templates Ctrl+Alt+T: ≥8 own templates; drop images, pan/zoom in zone, add/duplicate/move/remove sections; import/export `.shottemplate` (EDT-14).
- [ ] Paste / drag-drop images become Image objects; auto-flatten and keep-dimensions prefs (EDT-15).
- [ ] Esc closes editor (pref); keep running in background (pref); onboarding (EDT-16).

## 4. Effects (EFX)
- [ ] EFX-01 Border · EFX-02 Edges (all 7 styles, per side) · EFX-03 Shadow (8 dirs / all sides) · EFX-04 Perspective/Shear · EFX-05 Page curl.
- [ ] EFX-06 Colour adjust · EFX-07 Colour replace (pairs, tolerance, swap) · EFX-08 Filters (7) · EFX-09 Watermark · EFX-10 Capture-info caption.
- [ ] EFX-11 Resize (%, px/in/cm, DPI, smooth) · EFX-12 Rotate/flip · EFX-13 Trim (auto/manual) · EFX-14 Spotlight & Magnify · EFX-15 Set transparency.
- [ ] Each effect previews live, is undoable, and can be saved as a Quick Effect and used in a preset.

## 5. Video & GIF (MED)
- [ ] Playback: play/pause, frame step, scrub, volume/mute, thumbnails (MED-01).
- [ ] Trim + multiple cut-outs with undo; cannot cut whole video; Save Selection as New Video; Save Frame as Image; render is frame-accurate (MED-02).
- [ ] Combine Videos Ctrl+Shift+V with reorder (MED-03).
- [ ] [auto: engine] Create GIF presets (Screen Video 1280×720@5, High Motion 640×360@15, Reduced 400 px@3, Custom keeps source fps ≤ 30), dithering mode None / Floyd–Steinberg / Ordered, loop, fade to black, >30 s warning (MED-04).
- [ ] Animated GIF opens as video-like document (MED-05). MP4 metadata written (MED-06).

## 6. Vision (VIS) — offline
- [ ] OCR result dialog: rich/plain, Copy, Copy All, Save .txt, table copy as TSV/HTML; background OCR feeds search (VIS-01).
- [ ] Edit Text replaces text with matching editable objects over filled background (VIS-02).
- [ ] Smart Move (VIS-03), Auto Simplify detail 1–10 (VIS-04), Smart Redact categories + review pane (VIS-05), wand/lasso + auto-fill (VIS-06).
- [ ] Stitching quality on scrolling/panoramic (VIS-07); noise suppression host (VIS-08).
- [ ] Models download only on demand with SHA-256 check and licence display; app works without them (VIS-09).

## 7. Library (LIB)
- [ ] [P1] New captures autosave to the library (`.shotx` / `.mp4`) (LIB-01).
- [ ] [P1] Library grid; group/filter by date/app/website/type/tag; search hits name, app, title, URL, OCR, callout text, tags (LIB-02).
- [ ] Tags add/remove + filter; favourites (LIB-03).
- [ ] Change library location (move with progress, merge, missing-folder recovery) (LIB-04).
- [ ] Backup / import backup `.shotarchive` (LIB-05). Delete prompts and goes to Recycle Bin (LIB-06).
- [ ] Share history per item with copy/open link (LIB-07). Delete `library.db` → rebuilt from files; external file changes picked up (LIB-08).

## 8. Share (SHR)
- [ ] [P1] File: Save / Save As with format options; automatic naming tokens; overwrite prompt; save multiple; batch export with effects (SHR-01).
- [ ] [P1] Clipboard: bitmap + PNG (+ HTML pref); "prevent transparency" pref (SHR-02).
- [ ] Print with page setup, layout, multiple per page, preview, quick print (SHR-03) — also through the **Print share destination** (now uses `IPrintService`, not the shell verb).
- [ ] Share Accounts: client secret is stored as `Shotwise:{provider}:client-secret` in Credential Manager; Dropbox/Box/Imgur/X show a "Redirect port" (default 53682) that matches the registered redirect URI (SHR-08..12).
- [ ] A preset output binding with options (e.g. File destination with its own folder) uses those options for that capture only (CAP-14).
- [ ] Email `.eml` with inline/attached images (SHR-04). FTP/FTPS/SFTP upload (SHR-05). Program output with `{file}` (SHR-06).
- [ ] Word/Excel/PowerPoint/OneNote via COM; PowerPoint accepts GIF/MP4/step capture (SHR-07).
- [ ] OneDrive/SharePoint/Teams (SHR-08), Google Drive/YouTube (SHR-09), Dropbox/Box (SHR-10), Slack (SHR-11) with user-supplied client IDs.
- [ ] Imgur, S3-compatible, Custom HTTP uploader definition (SHR-12). Evernote/X documented decision (SHR-13).
- [ ] "Do not include capture source in names" pref; error messages for no file / multiple / in progress / cancelled / failed (SHR-14).
- [ ] Export formats: PNG, JPEG (quality, subsampling), BMP, GIF, TIFF multi-page, WebP, ICO, PDF (page options, searchable text), `.shotx`, MP4, animated GIF (SHR-15).

## 9. Non-functional
- [ ] 4K capture → editor < 1 s; UI never freezes during long operations (progress + cancel everywhere).
- [ ] No admin prompt except printer install.
- [ ] Clean-room check: no third-party product names, assets or text in UI, files or binaries.
