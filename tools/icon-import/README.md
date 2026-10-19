# icon-import (authoring-time tool)

Converts Fluent UI System Icons (MIT, Microsoft) into the WPF resource dictionaries used by Shotwise. It is not part of
`Shotwise.sln` and nothing it produces needs network access at runtime.

```powershell
dotnet run --project tools/icon-import -c Release -- tools/icon-import/icons.manifest src/Shotwise.UI.Common/Themes $env:TEMP\icon-catalog.md
```

- Input: `icons.manifest` (`Key | fluent-name | variants`; `r` Regular, `f` Filled, `c[:name]` Fluent Color, `t:#top,#bottom` Filled glyph
  tinted with a Shotwise hue gradient).
- Source: `https://unpkg.com/@fluentui/svg-icons@1.1.344/icons/<name>_<size>_<variant>.svg` (= GitHub commit
  `a347af95478f989038cccbf085978865264c048a`), cached in `%TEMP%\shotwise-icon-cache\1.1.344`.
- Output: `Icons.Regular.xaml` (20 px designs scaled into a 24-unit box), `Icons.Filled.xaml` (24 px), `Icons.Color.xaml`
  (24x24 `DrawingImage`, linear/radial gradients + gradientTransform kept) and a markdown key table (paste into
  `docs/DESIGN-SYSTEM.md` §5).
- Path data is re-emitted as compact relative WPF path markup rounded to 0.01 units; `fill-rule` maps to `F0`/`F1`.
- Pinning a newer icon release: bump `PackageVersion` + `SourceCommit` in `Program.cs` and update `THIRD-PARTY-NOTICES.md`.
