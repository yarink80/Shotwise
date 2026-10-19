# Shotwise — Feature Specification (Windows)

Shotwise is a free, open-source Windows application for screen capture, screen recording, annotation and sharing.
This document is its feature specification: it describes the **behaviour** users can rely on. Technical design and service
contracts are in [`ARCHITECTURE.md`](ARCHITECTURE.md).

**Original work.** Shotwise is a clean-room implementation written from this specification. Contributions must be original
or come from permissively licensed sources: never copy code, artwork, icons, sounds, templates or text from other products.
All artwork is our own (generated vector art) or comes from permissively licensed sets credited in
[`THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md). Third-party product names and trademarks are not used in the UI, code
identifiers or assets; hosted services are reached through generic, user-configured destinations (see SHR-*).

Each requirement has an ID (for example `CAP-01`); code comments, tests and [`SMOKE-TEST.md`](SMOKE-TEST.md) refer to these IDs.

---

## 1. Capture (CAP)

| ID | Requirement |
|---|---|
| CAP-01 | **All-in-One capture**: one hotkey/button → crosshair overlay on all monitors; click a window/element, drag a region, or pick fullscreen; then choose image, video, or scrolling from a mini toolbar next to the selection. |
| CAP-02 | Capture modes selectable in Capture Window: **All-in-One, Image, Video**. |
| CAP-03 | Selection types: **Region, Window, Fullscreen (monitor / all monitors), Fixed Region (W×H, optional X/Y), Freehand (lasso → alpha mask), Object (UI Automation element), Multiple Area (add several regions, commit → one image), Menu (cascaded menus, optional menu bar), Clipboard (take image from clipboard), Listen to Clipboard (auto-import images copied by other apps / system snipping tools), File (import image file).** |
| CAP-04 | Selection overlay: frozen dimmed snapshot of each monitor; crosshair (normal or simplified); **magnifier loupe** (toggle, positions to avoid edges); live W×H readout; window/element auto-highlight under cursor (DWM extended frame bounds, skip cloaked windows, ignore own windows); arrow keys nudge 1 px; Shift/Ctrl modifiers; **aspect locks 1:1 / 4:3 / 16:9** (key triggers); **guides** snapping to closest standard resolutions; resize/move selection before commit; Esc cancels; F1 help overlay. Minimum selection 8×8. |
| CAP-05 | **Scrolling capture (automatic)**: detect scrollable window/region (vertical, horizontal, both — hover arrows); scroll via UIA ScrollPattern / wheel / key / scrollbar messages with configurable delay; stitch frames (offset detection + sticky header/footer removal); "Start at top/left" option; optional **Require Start button**; failure dialog with troubleshooting. |
| CAP-06 | **Panoramic (manual scrolling) capture**: record region frames while user scrolls/pans manually; HUD with live stitched preview, Start/Stop/Cancel; produce one tall/wide image. |
| CAP-07 | **Grab Text capture**: select region → OCR → result dialog (see VIS-01). |
| CAP-08 | **Step capture**: record clicks across apps; each click captures a region around it and logs action (JSON); HUD shows step count, stop/resume/restart/cancel; result opens as a step-guide document (template with numbered Step annotations) and can be sent to outputs supporting step capture (PowerPoint). |
| CAP-09 | **Timing**: delay (5 s preset / custom seconds) with countdown; **interval** repeat (every N s/min/h); **schedule** at date/time. |
| CAP-10 | **Cursor**: include cursor in image captures (composited with hotspot); video cursor highlight (colour) and click animation (colour). |
| CAP-11 | **HDR**: optional "Support HDR color settings": capture HDR monitors as FP16 and tone-map to SDR (Direct2D HdrToneMap + WhiteLevelAdjustment using monitor SDR white level). |
| CAP-12 | **Multi-monitor & per-monitor DPI v2** throughout; capture in physical pixels. |
| CAP-13 | **Capture effects stack** applied automatically per preset (any EFX-* effect, ordered, editable). |
| CAP-14 | **After-capture outputs**: Copy to clipboard, Preview in Editor / Open in Editor, Open in (program), Share to (any SHR destination), with option toggles. |
| CAP-15 | **Presets**: named presets bundling mode, selection type + options, timing, cursor, effects, outputs, optional per-preset global hotkey; groups; create/save-current/edit/rename/duplicate/delete/reorder (drag); import/export (`.shotpresets` JSON); ship defaults: All-in-One, Image Region, Video, Grab Text, Panoramic, Step Capture, Fullscreen, Window. "Reset current settings". |
| CAP-16 | **Global hotkeys**: capture (default PrintScreen with conflict prompt for Windows' own PrintScreen→Snipping setting), repeat last capture, video start/pause/resume, video stop, ScreenDraw enter/exit, show/hide app, per-preset hotkeys; conflict detection (duplicate/unsupported/already registered), "Allow override other assignments", Restore Defaults. |
| CAP-17 | **Capture Widget** (OneClick): small edge-docked widget (auto-collapse) with Image/Video buttons, preset picker, options. |
| CAP-18 | **Tray icon**: menu — Capture, Open Editor, Open Capture Window, Widget toggle, Preferences, Exit; drag-drop images onto tray opens them. |
| CAP-19 | "Hide app during capture and remove its controls from captures"; remote-session masking warning; minimal startup latency (crosshair < 300 ms target). |
| CAP-20 | **Printer capture**: an installable virtual printer "Shotwise Printer"; printing from any app imports the pages as images into the editor. Settings: temp directory, close after capture, add/configure printer. Editor's Print must refuse to print to it (loop prevention). |

## 2. Video recording (REC)

| ID | Requirement |
|---|---|
| REC-01 | Record region / window / fullscreen to **MP4 (H.264 + AAC)**, hardware encoding when available ("Disable hardware accelerated recording" option). |
| REC-02 | Capture engine: Windows.Graphics.Capture (primary; borderless where allowed), fallback DXGI Desktop Duplication, fallback GDI BitBlt. Frame-rate governor; VFR timestamps from QPC. |
| REC-03 | Frame rate options 5/10/15/30/60 fps; quality setting; "Scale captures down to 1920×1080 (recommended)"; even/aligned dimension rounding. |
| REC-04 | **Microphone** (device picker, mute/unmute, VU meter, "alert when mic muted"), **System audio** (WASAPI loopback, toggle), mixed into one AAC track with sync after pause. |
| REC-05 | **Remove background noise** (mic) via neural denoiser (DeepFilterNet3 ONNX or RNNoise), toggle. |
| REC-06 | **Webcam**: device picker, PiP shapes (circle, rounded square, rounded rectangle), 4 corner placements, swap PiP↔fullscreen webcam during recording (Ctrl+Shift+F), live preview window. |
| REC-07 | Recording HUD: Ready-to-record window (optional), countdown (optional), Record / Pause / Resume / Stop / Restart / Cancel / redo selection, elapsed time, audio meters, minimized HUD when fullscreen on one monitor; device toggles disabled mid-recording where unsupported; "Automatically start recording" option. |
| REC-08 | **ScreenDraw**: annotate on screen while recording — Arrow (A), Square (S), Step (1), colour/theme, fade after N seconds, enter/exit hotkey. |
| REC-09 | **Cursor highlight & click animation** rendered into frames (works for every engine). |
| REC-10 | Disk-space guard (warn at threshold, stop at limit); errors: device busy/missing/privacy-denied, video disabled by policy, Media Feature Pack missing. |
| REC-11 | **GIF recording**: record then convert via GIF pipeline (MED-04). |
| REC-12 | **Video from Images**: narrate a set of still images (editor canvas region recorded with mic/system audio/webcam, pause/resume, background colour) → MP4. |

## 3. Editor — document, canvas, tools (EDT)

| ID | Requirement |
|---|---|
| EDT-01 | Document = raster background + ordered vector annotations (z-order), optional multiple pages (templates). Annotations stay editable until **Flatten** (Ctrl+T) / **Flatten All** (Ctrl+Shift+T) or export to flat formats. |
| EDT-02 | **Undo/Redo** unlimited (Ctrl+Z/Ctrl+Y), **Undo All**, visible Undo Stack window. Raster ops undoable (tile snapshots). |
| EDT-03 | Selection of objects: click, marquee, Ctrl/Shift multi-select, handles (resize, rotate), Select All (Ctrl+A), Deselect (Ctrl+D), Invert Selection (Ctrl+Shift+I), Delete, Cut/Copy/Paste (Ctrl+X/C/V), Copy All (Ctrl+Shift+C), Duplicate, Group (Ctrl+Shift+G)/Ungroup (Ctrl+Shift+U), Bring Forward (Ctrl+Shift+F)/Send Backward (Ctrl+Shift+B)/to front/back, lock, align/distribute, **canvas snapping** (Ctrl+;). |
| EDT-04 | Canvas: Resize Canvas (px/%/in/cm with 9-anchor), canvas colour (right-click with eraser/fill sets it), corner radius, canvas drop shadow, **auto-expand canvas** when objects dragged beyond edge (pref), transparent canvas. |
| EDT-05 | View: zoom in/out (Ctrl++/Ctrl+-), actual size (Ctrl+0), fit, zoom slider, pixel grid (Ctrl+G), pan (space-drag / middle), workspace colour (white…black/custom). |
| EDT-06 | **Tools** (each with Quick Styles + properties panel; tool hotkeys Ctrl+digit shown in tooltips): see table below. |
| EDT-07 | **Quick Styles** per tool, **Themes** (named set of quick styles + theme colours + font + shadow flag; create/import/export `.shottheme`; restore defaults), **Favorites** (star any quick style; Favorites tool shows them). Cursor quick-style popup (pref). |
| EDT-08 | **Colour picker**: RGB/HEX (#RRGGBB/#AARRGGBB), named colours, eyedropper (screen-wide), My Colors add/delete, palettes. |
| EDT-09 | **Shadow** editor (colour, angle/direction, distance, blur, opacity), opacity slider, outline on every object. |
| EDT-10 | **Text editing**: rich text runs (font, size, B/I/U/S, colour), outline colour/width, H/V alignment, padding, placeholder, floating format bar, auto-delete empty boxes, spell check (pref, language). |
| EDT-11 | **Tray** (bottom strip): open captures + recent library items (max configurable ≤200), pin/unpin (Ctrl+Shift+P), rename (F2), close (Ctrl+F4), delete to Recycle Bin (Ctrl+Del), drag-reorder, drag out as file (format pref), drag between items to combine; remove unpinned on close (pref). |
| EDT-12 | **New Image** (Ctrl+N, size + colour), **New from Clipboard** (Ctrl+Shift+N), Open (Ctrl+O; images, PDF pages, GIF/MP4, `.shotx`, best-effort `.snagx` import), Open Recent, Duplicate (Ctrl+Shift+D). |
| EDT-13 | **Combine Images** (Ctrl+Alt+C): ≥2 tray items → layout templates (grid, rows, columns…), caption, swap/zoom per slot. |
| EDT-14 | **Templates** (Create Image from Template, Ctrl+Alt+T): layouts made of header/body/footer sections with **drop zones** (drop images, pan/zoom inside), add/duplicate/move/remove sections, section guides, repeatable bodies; import/export `.shottemplate`; ship ≥8 own templates (step guide, comparison, before/after, grid 2/4/6, process, checklist). |
| EDT-15 | **Paste** images (auto-flatten pref), drag-drop images onto canvas as Image objects; "Keep original image dimensions" pref. |
| EDT-16 | Escape closes editor (pref); keep editor running in background (pref); onboarding/learning center (own content). |

### Tool table (EDT-06)

| Tool | Behaviour & properties |
|---|---|
| Arrow | start→end, optional Bezier bend; start/end caps (none, arrow, open, filled triangle, round, square, diamond, T-bar) with independent widths; colour, width, dash, opacity, shadow. |
| Line | as Arrow without heads; single/double line; Bezier. |
| Shape | rectangle, rounded rectangle (corner radius), ellipse, polygon; fill (or none), outline colour/width/dash, opacity, shadow; Shift constrains. |
| Callout | text in shape with 0..n tails (draggable tail points), ≥16 own shape presets (rect, round rect, ellipse, speech bubble, thought bubble, cloud, arrow-callouts, banner…); fill, outline, text props, padding, alignment, tail style/width/colour. |
| Text | rich text box (EDT-10). |
| Stamp | vector stamps (SVG/PDF) gallery: categories, search by keyword (localised), favourites, import packs (`.shotstamps` zip), opacity, shadow. Ship own starter set (arrows, badges, icons, emoji — Noto Emoji or Fluent Emoji under their licences). |
| Step | auto-numbered badges; sequence Number / A-Z / a-z; restart/edit value; styles circle, square, circle-pointer, square-pointer (pointer tail aimed by drag); fill/text colour, size, opacity, shadow. Next value persisted per document. |
| Pen | freehand; nibs circle/square/horizontal/vertical/diagonal; width, colour, opacity, smoothing, antialias, "create as vector". |
| Highlighter | rectangle/freehand highlight with multiply blend; colour, opacity. |
| Fill | flood fill flattened pixels with tolerance; colour/transparent; eyedropper. |
| Eraser | erase flattened pixels to canvas colour/transparent; width; antialias. |
| Magnify | lens (ellipse/rect/rounded) magnifying a source area; scale %, outline, connector line between source and lens, shadow. |
| Blur | region (rect/ellipse/rounded) blur / pixelate / solid; intensity; colour. Includes **Smart Redact** entry (VIS-05). |
| Crop | crop with handles, aspect, Apply / Cancel / Reset; "Delete cropped content" pref (otherwise non-destructive). |
| Cut Out | remove a horizontal or vertical strip and join; edge style None / Fade / Tear / Wave / Saw / Sharktooth. |
| Selection | rectangle / ellipse / polygon / freehand pixel selection; move/copy/cut/delete pixels; "Snap to object"; background after move: transparent or **auto-fill** (inpaint); speed/quality toggle. |
| Magic Wand | colour-based selection, tolerance, contiguous vs global colour selection, then same actions as Selection. |
| Move / Smart Move | move objects; **Smart Move** toggle detects UI components in the flattened image → movable/resizable/deletable objects with background auto-filled; detail/tolerance slider (auto); "no objects found" message (VIS-03). |
| Simplify | manual: draw simplified shapes (rect, rounded, ellipse, text bars L/C/R) from palette; **Auto Simplify**: detect text & UI regions → simplified shapes; detail slider, line spacing, text height, palette (image colours / custom / light / dark / grayscale), opacity, show original, lock objects, own placeholder stamps (avatar, image, video, cursor, window buttons) (VIS-04). |
| Grab Text / Edit Text | OCR selection or whole image (VIS-01); **Edit Text**: recognised text becomes editable text objects over inpainted background (VIS-02). |
| Cursor | insert cursor annotations (arrow, hand, I-beam…) as objects. |
| Favorites | shows starred quick styles across tools. |

## 4. Effects (EFX)

Applied to whole image (or selection where noted); each has live preview, can be saved as **Quick Effects** (user gallery) and used in capture presets (CAP-13).

| ID | Effect & parameters |
|---|---|
| EFX-01 | Border: colour, width (outside/inside). |
| EFX-02 | Edges: Drop shadow / Fade / Torn / Wave / Saw / Sharktooth / Bevel; per side (T/B/L/R); size; with shadow/border. |
| EFX-03 | Shadow: colour, direction (8 dirs or all sides), size/blur, opacity. |
| EFX-04 | Perspective / Shear: horizontal/vertical amount, border, shadow. |
| EFX-05 | Page Curl: corner, size. |
| EFX-06 | Colour Adjustment: brightness, contrast, hue, saturation, gamma; reset. |
| EFX-07 | Colour Replacement: list of old→new colour pairs, eyedropper, accuracy (tolerance), swap. |
| EFX-08 | Filters: grayscale, monochrome (threshold), invert, sharpen, soften/blur, emboss, sepia; intensity; opacity. |
| EFX-09 | Watermark: image file, scale, 9-position, overlay/underlay, transparency, emboss (direction/depth). |
| EFX-10 | Capture Info caption: app name/version, OS, timestamp, window title/URL, custom text, prompt at capture; font/size/colours; inside/outside, position. |
| EFX-11 | Resize/Scale: %, W/H (px/in/cm), aspect lock, DPI, smooth (Lanczos) scaling. |
| EFX-12 | Rotate 90/180/270/custom angle (resize canvas to fit), Flip H/V. |
| EFX-13 | Trim: auto-trim uniform borders; manual T/B/L/R. |
| EFX-14 | Spotlight & Magnify (selection required): dim/blur outside selection, optionally magnify selection. |
| EFX-15 | Set transparency (colour → transparent). |

## 5. Video & GIF editing (MED)

| ID | Requirement |
|---|---|
| MED-01 | Video playback in editor: play/pause, prev/next frame, scrubber with frame accuracy, volume/mute, thumbnails. |
| MED-02 | **Trim** (start/end handles), **Cut Out** middle selections (multiple cuts, undo/redo; cannot cut entire video), **Save Selection as New Video**, **Save Frame as Image**; render on save (frame-accurate re-encode). |
| MED-03 | **Combine Videos** (Ctrl+Shift+V): 2..N MP4s, reorder, join (re-encode to common size/fps). |
| MED-04 | **Create GIF** from whole video or selection: presets *Screen Video* (1280×720, 5 fps), *High Motion* (640×360, 15 fps), *Reduced File Size* (400 px wide, 3 fps), *Custom*; dimensions px/%; fps; looping; dithering; **fade to black**; >30 s warning. Encoder: per-frame/global palette quantisation, optional dithering, delta (changed-rect) frames, NETSCAPE loop block. |
| MED-05 | Animated GIF opened as a video-like document (playback, trim/cut, save frame, re-export). |
| MED-06 | MP4 metadata: title, comments, tags, capture date, source app. |

## 6. Vision / AI (VIS) — all local & offline (Windows OCR + OpenCV + optional open ONNX models)

| ID | Requirement |
|---|---|
| VIS-01 | **OCR / Grab Text**: Windows.Media.Ocr (primary, installed OS languages); optional Tesseract 5 engine (pref). Result dialog: rich/plain toggle, Copy, Copy All, Save .txt; errors (too large/out of memory). **Table extraction** from word boxes → TSV/HTML (copy as table). Background OCR of new captures (pref) feeding search + text tools. |
| VIS-02 | **Edit Text in image**: OCR lines → sample text colour, background colour, size → inpaint text region → editable Text objects with matched style. |
| VIS-03 | **Smart Move**: component detection on flattened image (colour-difference region growing / connected components, auto tolerance) → selectable components; move/resize/delete with background inpainting (OpenCV Telea/NS + patch-based for UI; optional LaMa ONNX "high quality"). |
| VIS-04 | **Auto Simplify**: text-block detection (morphology/MSER or OCR boxes) + UI rect detection + k-means dominant colours → simplified shapes with detail level 1–10. |
| VIS-05 | **Smart Redact**: categories Email, Phone, Credit card (Luhn), SSN, IP address (v4/v6), URL, Date, Postal address (heuristic), Face (OpenCV YuNet ONNX, fallback Haar cascade); review pane with per-category toggles + counts, redaction type (blur/pixelate/solid + colour), Done/Clear, "no data found". Produces Blur annotations. |
| VIS-06 | **Magic wand / lasso masks**, **selection auto-fill** (inpainting). |
| VIS-07 | **Stitching** engine for CAP-05/06: phase-correlation translation (with verification by similarity), sticky-border detection, blending, disk-backed frame store. |
| VIS-08 | **Noise suppression** model host for REC-05. |
| VIS-09 | Models: downloaded on first use to `%LOCALAPPDATA%\Shotwise\models` with SHA-256 verification and licence display; app fully functional (classical fallbacks) without them. |

## 7. Library & data (LIB)

| ID | Requirement |
|---|---|
| LIB-01 | **Auto-save** new image captures (`.shotx`) and video captures (`.mp4`) into the library folder (prefs, default on). |
| LIB-02 | Library view: grid of thumbnails; group/filter by date (year/month/day), application, website, media type, tag; search box (FTS over name, app, window title, URL, OCR text, callout text, tags); counts; multi-select; drag to tray/combine. |
| LIB-03 | **Tags**: add/remove on selected items; tag filter. Flags/favourites. |
| LIB-04 | **Library location**: choose folder (local or synced cloud folder), move library (with progress), merge existing, missing-folder recovery. |
| LIB-05 | **Backup / Import Backup** (`.shotarchive` = zip of library + catalog export). |
| LIB-06 | Prompt before delete (pref), delete to Recycle Bin. |
| LIB-07 | Share history: per item list of destination, link, time; copy/open link. |
| LIB-08 | Catalog = SQLite (+FTS5) rebuilt from files if lost; file watcher for external changes. Metadata per capture: capture date, app name/company/version, window title, URL (browser via UIA address bar when available), OS version, dimensions, duration, OCR text, tags. |

## 8. Output / Share (SHR)

Common: destination plugin contract (formats accepted, options dialog, prepare, async output with progress/cancel, result with link → copy to clipboard/open). Share button + Share menu in editor; capture presets can include outputs. Enable/disable destinations in prefs. Credentials in Windows Credential Manager (DPAPI). OAuth uses system browser + PKCE + loopback redirect with **user-supplied client IDs** configured in Preferences → Share → Accounts (we ship none).

| ID | Destination |
|---|---|
| SHR-01 | **File** (Save / Save As with format options; automatic naming: tokens date/time/app/window/counter, prefix, digits, start number, overwrite prompt; Save multiple images; **Batch export/convert** with effects). |
| SHR-02 | **Clipboard** (bitmap + PNG + optional HTML; "prevent clipboard transparency" pref). |
| SHR-03 | **Print** (printer, page setup, layout: fit/fill/scale/9-anchor, margins, orientation, multiple images per page grid, print preview, quick print). |
| SHR-04 | **Email** (generate `.eml`/MAPI draft with inline or attached images; recipient/subject/body presets). |
| SHR-05 | **FTP / FTPS / SFTP** (server, port, path, passive, auth, proxy, fixed/automatic filename). |
| SHR-06 | **Program** (user-configured executables with argument template `{file}`, eligible media types). |
| SHR-07 | **Word, Excel, PowerPoint, OneNote** via desktop COM (existing/new document, current/new slide/sheet/page, insert at cursor, embed vs link; PowerPoint accepts GIF/MP4 and step capture). |
| SHR-08 | **OneDrive, SharePoint, Teams** via Microsoft Graph + MSAL (folder picker, sharing link; Teams team/channel/chat + message). |
| SHR-09 | **Google Drive** (folder, link) and **YouTube** (title, description, tags, category, privacy; copy URL or embed code). |
| SHR-10 | **Dropbox**, **Box** (folder pick/create, shared link). |
| SHR-11 | **Slack** (workspace, channel/DM, message; user's own Slack app token). |
| SHR-12 | **Generic Uploaders**: **Imgur** (anonymous/account), **S3-compatible** (bucket, prefix, public URL template), **Custom HTTP uploader** (ShareX-style JSON definition: URL, method, form field, headers, response URL JSONPath). Plus **"Send to video editor"** = Program output preset. |
| SHR-13 | **Evernote / X (Twitter)**: implement only if current public APIs allow with user credentials; otherwise ship disabled with explanation (documented decision). |
| SHR-14 | "Do not include capture source in shared file names" pref; share errors (no file / multiple files / another share running / cancelled / failed). |
| SHR-15 | **Export formats**: PNG, JPEG (quality 1–100, subsampling), BMP, GIF (static), TIFF (multi-page), WebP, ICO, PDF (single/multi-page; page size, margins, orientation, layout, metadata; optional searchable text layer from OCR), `.shotx`, MP4, animated GIF. Bit depth/transparency options where applicable. |

## 9. Platform (PLT)

| ID | Requirement |
|---|---|
| PLT-01 | Single-instance app (capture + editor in one process), tray resident, run at Windows start (pref), command line: `Shotwise.exe [file…] [--capture[=preset]] [--editor] [--background]`. |
| PLT-02 | **Preferences** (Capture: General/Capture/Hotkeys; Editor: General/Editor/Library/Share/Advanced/Experiments) — every capture, editor, library, share and advanced option with our own defaults; Reset to defaults; theme Dark/Light/System; language (en-US first; resx structure for de, es, fr, ja, pt-BR). |
| PLT-03 | **Toast notifications** (capture saved, share completed with link actions), product help/notification preference (all / help only / none). |
| PLT-04 | **Updater**: in-place updates from the project's GitHub Releases (Velopack): check (auto/ask first/days between checks), background download (never on metered connections), apply on restart or exit; Stable/Beta channel; disabled in development builds. See [`UPDATES.md`](UPDATES.md). |
| PLT-05 | **Mobile Connect**: "Allow sharing to Shotwise" — local HTTPS server (port 8303 default, configurable), QR pairing (expiring token), phone opens served PWA page and uploads photos/videos → appear in tray/library; connection name. |
| PLT-06 | **Shell integration**: `.shotx` file association, Explorer thumbnails (+ properties: app, window, URL, OCR text) via native shell extension, "Edit in Shotwise" context menu, `shotwise://` URI scheme (import stamps/templates/themes). |
| PLT-07 | Usage telemetry: **off by default**, opt-in only, local log viewer — no network endpoint shipped. Crash dumps written locally. |
| PLT-08 | Feature switches (JSON + policy registry `HKLM\Software\Policies\Shotwise`: allow OCR, outputs, system audio, mic, mobile, FTP, updates, telemetry). |
| PLT-09 | Accessibility: full keyboard operation, UI Automation names, high-contrast support. |
| PLT-10 | Logging to `%LOCALAPPDATA%\Shotwise\logs` (rolling). |

## 10. Non-functional

- Windows 10 2004 (19041)+ / Windows 11, x64. .NET 8 (retarget to .NET 10 LTS via one property).
- Crosshair appears < 300 ms after hotkey; image capture → editor < 1 s for 4K.
- No admin rights needed except printer install (elevated helper on demand).
- All long operations async with progress + cancellation; UI never blocks.
- Unit tests for Core/Editor/Vision/Media/Library logic; manual smoke checklist in `docs/SMOKE-TEST.md`.
