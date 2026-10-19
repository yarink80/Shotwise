# icon-gen (authoring-time tool)

Renders the original Shotwise app icon. Not part of `Shotwise.sln`; nothing it produces is generated at runtime.

```powershell
dotnet run --project tools/icon-gen -c Release -- <repo-root> [<preview-dir>]
```

The artwork is defined in code (`Program.cs`, `Spec.For(size)`): a rounded indigo-violet tile (`#7C70FF` top-left to `#5B4CF5`
bottom-right) with a soft top highlight, four white viewfinder corner brackets, and a coral shutter dot (`#FF7A45` to `#FF4D5E`)
with an inner highlight. Each size has its own geometry so strokes land on whole pixels:

| Size | Detail level |
|---|---|
| 16 | 1 px square brackets, no rounded bends, flat dot |
| 20, 24 | 2 px square brackets, flat dot |
| 32 | 3 px brackets with a 1 px bend, dot highlight |
| 40 and up | round caps and bends, dot shadow, hairline inner edge from 64 px |

Outputs (all overwritten):

- `src/Shotwise.App/Assets/shotwise-icon.svg`: master artwork (256 grid).
- `src/Shotwise.App/Assets/svg/shotwise-<n>.svg`: the pixel-hinted design per size, plus `shotwise-recording-32.svg`.
- `src/Shotwise.App/Assets/png/shotwise-<n>.png`: 16, 20, 24, 32, 40, 48, 64, 96, 128, 256.
- `src/Shotwise.App/Assets/shotwise.ico`: all ten sizes (32-bpp DIB entries, PNG entry at 256). Used as `ApplicationIcon`
  (exe, taskbar, Alt+Tab, Start menu shortcut, `.shotx` DefaultIcon) and as every window's icon.
- `src/Shotwise.App/Assets/shotwise-tray.ico` and `shotwise-tray-recording.ico`: notification-area icons at 16 to 64 px. The
  recording state drops the brackets at tray sizes and shows a larger coral dot with a white ring.
- `<preview-dir>/app-icon-sizes.png` (default `docs/screenshots`, not tracked by git): contact sheet (all sizes on light
  and dark, tray sizes magnified x6).

The colour icon stays readable at 16 px (white 1 px brackets on violet, coral dot), so no separate monochrome tray icon is used.
