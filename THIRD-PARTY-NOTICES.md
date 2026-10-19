# Third-party notices

Shotwise is built from original code. It uses the open-source components listed below, each under its own
licence. Binary redistributions must ship this file (plus the full licence texts referenced) next to the
executable.

**Rules for contributors**
- Only permissively licensed components (MIT, Apache-2.0, BSD, MS-PL, OFL for fonts/emoji, zlib, ISC) may be
  added. Copyleft licences (GPL, LGPL for static use, AGPL) are not allowed.
- New packages are pinned centrally in `Directory.Packages.props` and recorded here in the matching section, in the same
  pull request that introduces them.
- Assets (icons, stamps, templates, sounds, models) that are not our own need an entry here with source URL,
  licence and attribution text.

---

## NuGet packages (pinned in `Directory.Packages.props`)

Licence information is taken from each package's NuGet metadata (resolved 2026-10-17).

| Package | Version | Licence | Project / copyright |
|---|---|---|---|
| Microsoft.Extensions.Hosting / Logging / Logging.Abstractions / DependencyInjection.Abstractions | 10.0.13 | MIT | .NET Foundation and contributors — https://github.com/dotnet/runtime |
| WPF-UI | 4.3.0 | MIT | lepo.co (Leszek Pomianowski) — https://github.com/lepoco/wpfui |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | .NET Foundation and contributors — https://github.com/CommunityToolkit/dotnet |
| H.NotifyIcon.Wpf (+ H.NotifyIcon) | 2.3.2 | MIT | havendv — https://github.com/HavenDV/H.NotifyIcon |
| Microsoft.Toolkit.Uwp.Notifications | 7.1.3 | MIT | .NET Foundation — https://github.com/CommunityToolkit/WindowsCommunityToolkit |
| SkiaSharp, SkiaSharp.Views.WPF, SkiaSharp.HarfBuzz (+ HarfBuzzSharp, native assets) | 3.119.2 (HarfBuzzSharp 8.3.1.x) | MIT | Microsoft — https://github.com/mono/SkiaSharp (bundles Skia © Google, BSD-3-Clause; HarfBuzz, MIT-style "Old MIT") |
| Svg.Skia (+ Svg.Model, Svg.SceneGraph, Svg.Animation, ShimSkiaSharp, ExCSS) | 5.1.1 | MIT | Wiesław Šoltés — https://github.com/wieslawsoltes/Svg.Skia |
| Svg.Custom (transitive, via Svg.Skia) | 5.1.1 | MS-PL | SVG rendering library contributors — https://github.com/svg-net/SVG |
| PDFsharp | 6.2.4 | MIT | empira Software GmbH / PDFsharp Team — https://docs.pdfsharp.net/ (pinned below 6.5.0, which is AGPL-3.0-only) |
| Microsoft.Windows.CsWin32 (build-time source generator) | 0.3.363 | MIT | Microsoft — https://github.com/microsoft/CsWin32 |
| Vortice.Direct3D11, Vortice.DXGI, Vortice.Direct2D1, Vortice.MediaFoundation | 3.8.3 | MIT | Amer Koleci — https://github.com/amerkoleci/Vortice.Windows |
| Interop.UIAutomationClient | 10.19041.0 | MIT | Roman (Roemer) — https://github.com/Roemer/UIAutomation-Interop |
| NAudio | 2.4.0 | MIT | Mark Heath & contributors — https://github.com/naudio/NAudio |
| OpenCvSharp4, OpenCvSharp4.runtime.win | 4.13.0.20260627 | Apache-2.0 | shimat — https://github.com/shimat/opencvsharp (bundles OpenCV, Apache-2.0) |
| Microsoft.ML.OnnxRuntime (+ .Managed) | 1.31.0 | MIT | Microsoft — https://github.com/microsoft/onnxruntime |
| Microsoft.Data.Sqlite | 10.0.13 | MIT | .NET Foundation — https://github.com/dotnet/efcore |
| SQLitePCLRaw (bundle_e_sqlite3, core, provider, lib) — transitive | 2.1.x | Apache-2.0 | Eric Sink — https://github.com/ericsink/SQLitePCL.raw (bundles SQLite, public domain) |
| Microsoft.Identity.Client, Microsoft.Identity.Client.Extensions.Msal | 4.91.0 | MIT | Microsoft — https://github.com/AzureAD/microsoft-authentication-library-for-dotnet |
| FluentFTP | 55.1.0 | MIT | Robin Rodricks & contributors — https://github.com/robinrodricks/FluentFTP |
| SSH.NET | 2026.0.0 | MIT | Renci / SSH.NET contributors — https://github.com/sshnet/SSH.NET |
| MimeKit (+ BouncyCastle.Cryptography transitive, MIT) | 4.18.1 | MIT | Jeffrey Stedfast — https://github.com/jstedfast/MimeKit |
| QRCoder | 1.8.1 | MIT | Raffael Herrmann, Shane Krueger — https://github.com/codebude/QRCoder |
| xunit, xunit.runner.visualstudio (tests only, not redistributed) | 2.9.3 / 4.0.1 | Apache-2.0 | .NET Foundation — https://github.com/xunit/xunit |
| Microsoft.NET.Test.Sdk (tests only, not redistributed) | 18.11.0 | MIT | Microsoft — https://github.com/microsoft/vstest |

P3 integration (2026-10-18): Microsoft.Graph, Google.Apis.Drive.v3, Google.Apis.YouTube.v3, Dropbox.Api and Box.V2.Core
(and with them the transitive Microsoft.Kiota.*, Azure.Core, Google.Apis.* and Newtonsoft.Json) were removed — the share
destinations use the vendors' documented REST APIs over `HttpClient`, so none of these SDKs is shipped any more.

### Licence texts

- MIT: https://opensource.org/license/mit
- Apache-2.0: https://www.apache.org/licenses/LICENSE-2.0
- MS-PL: https://opensource.org/license/ms-pl-html
- BSD-3-Clause (Skia): https://opensource.org/license/bsd-3-clause

The full texts ship with every installed copy in the `licenses\` folder next to `Shotwise.exe`: the generic texts from
the repository's `licenses/` folder (MIT, Apache-2.0, BSD-3-Clause, MS-PL, OFL-1.1, Zlib, Unicode-3.0) plus, in
`licenses\packages\`, the upstream licence and notice files of every shipped NuGet package (collected by `tools/package.ps1`).

### Packaging and transitive runtime components (added 2026-10-18, version 0.9.0)

| Package | Version | Licence | Project / copyright |
|---|---|---|---|
| Velopack (installer, updater runtime, `Update.exe`, `Setup.exe` stub) | 1.2.161 | MIT | Velopack Ltd, Caelan Sayler, Kevin Bost — https://github.com/velopack/velopack |
| vpk (build-time packaging tool, not redistributed) | 1.2.161 | MIT | same |
| OpenTK (+ .Graphics, .Windowing.*, .Mathematics, .Compute, .Input, .OpenAL, .Core) — via SkiaSharp.Views.WPF (GPU canvas) | 4.3.0 | MIT | The Open Toolkit library contributors — https://github.com/opentk/opentk |
| OpenTK.GLWpfControl | 4.2.3 | MIT | OpenTK contributors — https://github.com/opentk/GLWpfControl |
| OpenTK.redist.glfw (`glfw3.dll`) | 3.3.0-pre20200830200122 | Zlib | GLFW © Marcus Geelnard, Camilla Löwy — https://www.glfw.org/ |
| NAudio sub-packages (Asio, Core, Midi, Wasapi, WinForms, WinMM) | 2.4.0 | MIT | Mark Heath & contributors |
| SharpGen.Runtime, SharpGen.Runtime.COM (via Vortice) | 2.4.2-beta | MIT | SharpGenTools contributors — https://github.com/SharpGenTools/SharpGenTools |
| H.GeneratedIcons.System.Drawing (via H.NotifyIcon) | 2.3.2 | MIT | havendv |
| .NET 8 runtime, ASP.NET Core and Windows Desktop runtimes (self-contained, win-x64) | 8.0.28 | MIT | .NET Foundation and contributors — https://github.com/dotnet |
| Microsoft Visual C++ runtime, app-local (`msvcp140.dll`, `msvcp140_1.dll`, `vcruntime140.dll`, `vcruntime140_1.dll`; needed by onnxruntime and glfw) | 14.44 (MSVC v143) | Microsoft Visual Studio "Distributable Code" (redistributable, not open source) | © Microsoft Corporation — https://learn.microsoft.com/cpp/windows/redistributing-visual-cpp-files |

Not shipped: `opencv_videoio_ffmpeg*.dll` (OpenCV's FFmpeg video-IO plugin from OpenCvSharp4.runtime.win, LGPL FFmpeg) is
removed by `tools/package.ps1` because Shotwise never uses OpenCV video I/O. Native PDBs, import libraries and
`createdump.exe` are removed as well.

### Platform components (not redistributed)

WinRT projections for Windows.Graphics.Capture, Windows.Media.Ocr, Windows.Media.Capture and Windows.Data.Pdf come
from the .NET Windows SDK targeting pack (Microsoft.Windows.SDK.NET.Ref, selected by the TFM) and the operating system.
Segoe Fluent Icons / Segoe MDL2 Assets are Windows system fonts used by reference only (not embedded).

---

<!-- Append a "## <topic>" section below for every package or asset you add. -->

## Vision — downloadable ML models (not bundled; fetched only on explicit user action)

`Shotwise.Vision` ships **no model files**. `IModelManager.EnsureAsync` downloads a model to `%LOCALAPPDATA%\Shotwise\models` only when the user
asks for it (VIS-09); size and SHA-256 are verified before the file becomes usable and the licence is shown first. The manifest is
`src/Shotwise.Vision/models.manifest.json` (values verified against the live hosts on 2026-10-17).

| Model id | File | Source | SHA-256 | Size | Licence | Used for |
|---|---|---|---|---|---|---|
| `yunet` | `face_detection_yunet_2023mar.onnx` | https://github.com/opencv/opencv_zoo/tree/main/models/face_detection_yunet (YuNet by Wei Wu / Shiqi Yu, libfacedetection) | `8f2383e4dd3cfbb4553ea8718107fc0423210dc964f9f4280604804ed2552fa4` | 232,589 B | MIT | Smart Redact face detection (OpenCV `FaceDetectorYN`) |
| `lama` | `lama_fp32.onnx` | https://huggingface.co/Carve/LaMa-ONNX (ONNX export of advimman/lama, "Resolution-robust Large Mask Inpainting with Fourier Convolutions") | `1faef5301d78db7dda502fe59966957ec4b79dd64e16f03ed96913c7a4eb68d6` | 208,044,816 B | Apache-2.0 | Optional "High quality" inpainting (Smart Move, Edit Text, selection auto-fill) |

Optional, never downloaded: a user-supplied streaming noise-suppression ONNX placed as `noise-suppressor.onnx` in the models folder
(see `INoiseSuppressor` in `Shotwise.Core`); its licence is the responsibility of whoever supplies it (DeepFilterNet is MIT/Apache-2.0, RNNoise is BSD-3-Clause).

Algorithms re-implemented from the public literature (no third-party code): Criminisi et al., "Region Filling and Object Removal by
Exemplar-Based Image Inpainting" (IEEE TIP 2004); Telea fast-marching and Navier-Stokes inpainting (via OpenCV `cv::inpaint`);
Kuglin & Hines phase correlation. OCR uses the Windows.Media.Ocr operating-system component (not redistributed).

## Rendering, effects, codecs, PDF

No third-party art or fonts are bundled. All callout shapes, cursor glyphs, edge masks (torn / wave / saw / sharktooth), page curl, bevel,
step badges and Simplify placeholders are original procedural geometry authored for Shotwise. Libraries used (already pinned in
`Directory.Packages.props`): SkiaSharp + SkiaSharp.HarfBuzz (MIT / BSD-3 Skia / MIT HarfBuzz), Svg.Skia (MIT; SVG stamps), PDFsharp 6.2.4 (MIT — do not
upgrade past the MIT line). WIC (System.Windows.Media.Imaging) and Windows.Data.Pdf are operating-system components (not redistributed).
Text is drawn with fonts installed on the user's system (default family "Segoe UI"); none are embedded in the app.

## Design system — UI icons (2026-10-18)

| Asset | Licence | Source |
|---|---|---|
| Fluent UI System Icons — *Regular*, *Filled* and *Color* SVG glyphs (99 icons, see `docs/DESIGN-SYSTEM.md` §5) converted to XAML geometry / DrawingImage in `src/Shotwise.UI.Common/Themes/Icons.Regular.xaml`, `Icons.Filled.xaml`, `Icons.Color.xaml` | MIT | © Microsoft Corporation — https://github.com/microsoft/fluentui-system-icons, commit `a347af95478f989038cccbf085978865264c048a` (release 1.1.344, npm `@fluentui/svg-icons@1.1.344`) |

Conversion is done at authoring time by `tools/icon-import` (no runtime SVG dependency). Tinted tool icons are the Fluent *Filled*
glyph painted with Shotwise's own hue gradients. The Shotwise app mark (`Icon.Brand.Color`), the gallery sample artwork and every colour
token are original. MIT licence text (Microsoft Corporation):

> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the
> "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish,
> distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to
> the following conditions: The above copyright notice and this permission notice shall be included in all copies or substantial portions
> of the Software. THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
> WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
> LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
> CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## Original built-in artwork (2026-10-17)
The 126 SVG stamps, six cursor SVGs, and eight Shotwise templates under Content/stamps, Content/cursors, and Content/templates are original procedural/vector artwork authored for Shotwise. No external artwork, emoji font outlines, or proprietary assets were copied. SVG text uses the installed Segoe UI/sans-serif font at rendering time; font files are not redistributed. No additional third-party artwork licence is required. Generators are maintained under Shotwise.UI.Dialogs/Tools. Existing WPF-UI (MIT) and CommunityToolkit.Mvvm (MIT) package notices above also apply to this dialog implementation.

## Stamps v2 — built-in stamp packs (2026-10-18)

Supersedes the stamp part of the "Original built-in artwork" entry above: `src/Shotwise.App/Content/stamps/**` is now
produced by `tools/stamp-gen` (authoring-time only; usage in the header of `tools/stamp-gen/Program.cs`).

| Asset | Licence | Source / attribution |
|---|---|---|
| 1,595 emoji SVGs in `Content/stamps/emoji-*` (Color style, default skin tone; re-serialized: numbers rounded, ids shortened, size attributes set) | MIT | **Fluent Emoji** © Microsoft Corporation — https://github.com/microsoft/fluentui-emoji, commit `1ffb34c752ecf5d402f04cfb4b392c77f57c54bc` |
| Localized emoji names/keywords (de, es, fr, ja, pt-BR) in the emoji `packageinfo.json` files; emoji sort order | Unicode License v3 | Unicode CLDR annotations (`cldr-json` tag `48.2.3`, https://github.com/unicode-org/cldr-json) and `emoji-test.txt` 16.0 (https://www.unicode.org/Public/emoji/16.0/) © Unicode, Inc. |
| Glyph outlines converted to SVG paths inside our own stamps (no font file is shipped) | SIL OFL 1.1 | Fira Sans (Bold/SemiBold/Medium) © Mozilla Foundation / Carrois; Anton © Vernon Adams; Bangers © Vernon Adams — via https://github.com/google/fonts commit `47d54ca1ebb14fd864695f1d39dc734f39017c33` |
| Glyph outlines converted to SVG paths (hand-drawn ?, !, digits) | Apache-2.0 | Permanent Marker © Font Diner — same google/fonts commit |

All other stamps (labels, badges, arrows, marks, stickers, keyboard, interface — 1,021 files) are original artwork generated
from code in `tools/stamp-gen`; the Windows-style key uses a generic window glyph, not a trademark logo. No artwork, word lists
or file names from any commercial stamp library were used.

MIT licence (Fluent Emoji), copyright line "Copyright (c) Microsoft Corporation." — text identical to the MIT text quoted in the
D1 section above. Unicode License v3: permission is granted, free of charge, to use, copy, modify, merge, publish, distribute and/or
sell copies of the data files, provided the copyright and permission notice appear in all copies (full text:
https://www.unicode.org/license.txt). The OFL permits embedding rendered glyph outlines in artwork; the fonts themselves are not
redistributed.
