# Shotwise design system

The tokens, controls and screen recipes every Shotwise window is built from.

Owner: `src/Shotwise.UI.Common` (Themes/, Controls/, Theming/, Gallery/).
Living gallery: `Shotwise.UI.Common.Gallery.StyleGalleryView` renders every token and control in both themes
(`StyleGalleryRenderer.Render(element, path)` writes it to a PNG).

## 1. Setup — what you get for free

- `Resources/SharedResources.xaml` (already merged by `App.xaml` after the WPF-UI dictionaries) now merges
  `Themes/Shotwise.xaml` = tokens + metrics + icons + component styles. No App change is needed.
- XAML namespace: `xmlns:sw="clr-namespace:Shotwise.UI.Common.Controls;assembly=Shotwise.UI.Common"`
  (or `xmlns:sw="urn:shotwise-ui"`, which also maps `Shotwise.UI.Common.Theming`).
- Theme: `IThemeService` (unchanged API) now also swaps the Shotwise token dictionary (`ShotwiseTheme.Apply`) and sets the
  WPF-UI accent to the brand violet in both themes (white text on accent). **Default theme = Light**
  (`GeneralSettings.Theme` default changed in Core). Light / Dark / System stay in Preferences; High contrast maps the tokens
  to Windows system colours.
- Gallery: `new Shotwise.UI.Common.Gallery.StyleGalleryWindow().Show()` (public; for a hidden `--gallery` switch the App may add),
  `StyleGalleryRenderer.RenderAll(dir)` writes the two PNGs, `StyleGallery.ApplyTheme(EffectiveTheme)` for tools/tests.
- Custom controls also have theme-level defaults in `Themes/Generic.xaml` (Light tokens) so they render even in tool harnesses
  that did not merge SharedResources — but always merge SharedResources in real windows.

## 2. Tokens (always `{DynamicResource ...}` — they are swapped at runtime)

| Token (brush key) | Light | Dark | Use |
|---|---|---|---|
| `Shotwise.AccentBrush` | `#5B4CF5` | `#7C70FF` | primary buttons, selection rings, focus ring, switch on, slider fill |
| `Shotwise.AccentHoverBrush` / `AccentPressedBrush` | `#4A3BE0` / `#3C2FC7` | `#8F85FF` / `#6A5DF0` | primary hover / pressed |
| `Shotwise.AccentTextBrush` | `#4A3BE0` | `#ABA4FF` | accent text on surfaces (selected labels, links) — ≥ 4.5:1 |
| `Shotwise.AccentSubtleBrush` / `AccentSubtleHoverBrush` | accent 12 % / 18 % | accent 18 % / 25 % | selected tool / nav pill background |
| `Shotwise.OnAccentBrush` | `#FFFFFF` | `#FFFFFF` | text/icons on accent fills |
| `Shotwise.AccentGradientBrush` | `#7A6CFF→#4F3FEA` | `#988EFF→#6A5DF0` | hero tiles, brand surfaces (sparingly) |
| `Shotwise.CaptureBrush` (+`HoverBrush`, `PressedBrush`) | `#FF7A45→#FF4D5E` | same | capture button, record |
| `Shotwise.CaptureSolidBrush` / `CaptureRingBrush` | `#FF4D5E` / coral 14 % | `#FF4D5E` / coral 20 % | record dot, capture nav dot, halo |
| `Shotwise.SuccessBrush` / `WarningBrush` / `DangerBrush` | `#16A34A` / `#F59E0B` / `#E11D48` | `#22C55E` / `#FBBF24` / `#FB4D6D` | status fills, badges |
| `Shotwise.{Success,Warning,Danger,Info}SubtleBrush` | tinted pastel | tinted dark | InfoBar backgrounds |
| `Shotwise.{Success,Warning,Danger}TextBrush` | darker hue | lighter hue | status text on surfaces |
| `Shotwise.DangerHoverBrush` | `#C8163E` | `#FF6681` | danger button hover |
| `Shotwise.Surface0Brush` | `#F6F7FB` | `#16171C` | window background |
| `Shotwise.Surface1Brush` | `#FFFFFF` | `#1F2027` | panels, cards, bars, popups |
| `Shotwise.Surface2Brush` | `#EEF0F7` | `#2A2B34` | hover, segmented track, chips |
| `Shotwise.Surface3Brush` | `#E3E6F0` | `#34363F` | pressed, slider track, disabled fill |
| `Shotwise.CanvasBrush` | `#E9EBF2` | `#101116` | editor workspace behind the image |
| `Shotwise.BorderSubtleBrush` / `BorderStrongBrush` | `#E3E5EE` / `#CDD1DE` | `#2F313B` / `#424552` | 1 px dividers / input & swatch borders |
| `Shotwise.TextPrimaryBrush` / `TextSecondaryBrush` / `TextTertiaryBrush` | `#14151A` / `#5A5F73` / `#8A8FA3` | `#F3F4F8` / `#B4B8C8` / `#7E8396` | text hierarchy (tertiary = hints/captions only) |
| `Shotwise.TextDisabledBrush` | `#B2B6C4` | `#565A69` | disabled text |
| `Shotwise.HudBrush` / `HudTextBrush` | `#E61B1C22` / `#F3F4F8` | `#E60E0F13` / `#F3F4F8` | recording HUD, floating dark pills |
| `Shotwise.ScrimBrush` | `#66101118` | `#99000000` | overlays behind modal content |
| `Shotwise.SwitchOffTrackBrush` / `SwitchKnobBrush` | `#C9CDDA` / white | `#4A4D5A` / white | switch |

Every brush also has a `Color` twin: `Shotwise.Color.Accent`, `.AccentHover`, `.AccentPressed`, `.AccentText`, `.AccentSubtle`,
`.Capture`, `.CaptureEnd`, `.CaptureGlow`, `.Success`, `.Warning`, `.Danger`, `.Info`, `.Surface0..3`, `.Canvas`, `.BorderSubtle`,
`.BorderStrong`, `.TextPrimary/Secondary/Tertiary/Disabled`, `.Shadow`. `Shotwise.ThemeName` (string) = Light / Dark / HighContrast.

Elevation (`DropShadowEffect`, theme-tuned): `Shotwise.Elevation.Raised` (thumbs, segment lift), `.Card`, `.Flyout`
(popups, toasts), `.CaptureGlow`. **Put effects on a separate layer behind content** (as `sw:Card` does) — never on a
container with text (blurs ClearType).

### Metrics (`Themes/Metrics.xaml`, StaticResource ok)
- Radius: `Shotwise.Radius.Control` 6, `.Small` 4, `.Tile` 8, `.Card` 10, `.CaptureCard` 14, `.Pill` 999.
- Spacing 4/8/12/16/24: `Shotwise.Space.4` … `.24` (Thickness), `Shotwise.Space.Value.4` … (double).
- Sizes: `Shotwise.Size.TopBarHeight` 56, `.ControlHeight` 32, `.SidePanelWidth` 312, `.Icon.Small` 16, `.Icon.Chrome` 20, `.Icon.Tool` 24, `.Icon.Large` 32.
- Type: `Shotwise.FontFamily` (Segoe UI Variable Text → Segoe UI), `.FontFamily.Display`; sizes Display 28, Title 20, Subtitle 15,
  Section 13, Body 13, Caption 11.
- Text styles (TextBlock): `Shotwise.Text.Display`, `.Title`, `.Subtitle`, `.Section`, `.Body`, `.Secondary`, `.Caption`, `.Overline`, `.Accent`.

## 3. Components

All styles are theme-aware, have keyboard focus visuals (`Shotwise.FocusVisual` / `.Round`) and set automation names where
the control knows its label (tooltips "Label (Shortcut)" are automatic for `ToolButton`, `NavItem`, `CaptureButton`).
Icon-only buttons: **always** set `AutomationProperties.Name` and `ToolTip`.

### Buttons (keyed styles for `Button` / `ToggleButton`; icon via `sw:SwUi.Icon`, size `sw:SwUi.IconSize`, default 16)
```xml
<Button Style="{StaticResource Shotwise.PrimaryButton}" Content="Save" IsDefault="True" />
<Button Style="{StaticResource Shotwise.PrimaryButton}" sw:SwUi.Icon="{StaticResource Icon.Gif.Regular}" Content="Create GIF" />
<Button Style="{StaticResource Shotwise.SecondaryButton}" Content="Cancel" IsCancel="True" />
<Button Style="{StaticResource Shotwise.GhostButton}" sw:SwUi.Icon="{StaticResource Icon.Copy.Regular}" Content="Copy All" />
<Button Style="{StaticResource Shotwise.DangerButton}" sw:SwUi.Icon="{StaticResource Icon.Delete.Regular}" Content="Delete" />
<Button Style="{StaticResource Shotwise.IconButton}" sw:SwUi.Icon="{StaticResource Icon.Help.Regular}"
        ToolTip="Help (F1)" AutomationProperties.Name="Help" />               <!-- 32 px round; works on ToggleButton too -->
<ToggleButton Style="{StaticResource Shotwise.GhostToggleButton}" sw:SwUi.Icon="{StaticResource Icon.Effects.Regular}" Content="Effects" />
<Button Style="{StaticResource Shotwise.ToolCommandButton}" sw:SwUi.Icon="{StaticResource Icon.Undo.Regular}" Content="Undo"
        Command="{Binding UndoCommand}" ToolTip="Undo (Ctrl+Z)" />            <!-- tool-bar look for commands (Undo/Redo/More) -->
```
`sw:SwUi.CornerRadius` overrides the radius; `sw:SwUi.Shortcut` is available for your own templates.

### ToolButton (editor tools; a RadioButton — tools in one panel are exclusive)
```xml
<sw:ToolButton Icon="{StaticResource Icon.Tool.Arrow.Color}" Content="Arrow" Shortcut="A"
               IsChecked="{Binding IsArrowActive}" Command="{Binding SelectToolCommand}" CommandParameter="Arrow" />
```
56×52, 24 px colour icon, 11 px caption; hover Surface2; checked = accent pill + accent semibold caption. Use `GroupName`
when tools are spread over several panels. `IconSize` default 24.

### CaptureButton / SplitButton
```xml
<sw:CaptureButton Content="Capture" Shortcut="Print Screen" Command="{Binding CaptureCommand}" />   <!-- Diameter=104 default -->
<sw:CaptureButton Content="Record" Icon="{StaticResource Icon.Record.Filled}" Diameter="96" />
<sw:SplitButton Icon="{StaticResource Icon.Share.Regular}" Content="Share" Command="{Binding ShareDefaultCommand}"
                DropDownToolTip="Share destinations" DropDownOpening="OnShareMenuOpening">
    <sw:SplitButton.DropDownMenu>
        <ContextMenu>
            <MenuItem Header="Save to file" Icon="{StaticResource ...}" />   <!-- use sw:SwIcon as MenuItem.Icon -->
        </ContextMenu>
    </sw:SplitButton.DropDownMenu>
</sw:SplitButton>
```

### NavItem — SegmentedNav, ModeRail, left nav lists (RadioButton)
```xml
<Border Style="{StaticResource Shotwise.SegmentedNav}">            <!-- Surface2 track; checked item lifts to Surface1 -->
  <StackPanel Orientation="Horizontal">
    <sw:NavItem Icon="{StaticResource Icon.Editor.Regular}" Content="Editor" IsChecked="True" />
    <sw:NavItem Icon="{StaticResource Icon.Library.Regular}" Content="Library" />
    <sw:NavItem Icon="{StaticResource Icon.Capture.Regular}" Content="Capture" ShowDot="True" />
    <sw:NavItem Icon="{StaticResource Icon.Create.Regular}" Content="Create" />
  </StackPanel>
</Border>
<sw:NavItem Style="{StaticResource Shotwise.ModeRailItem}" Icon="{StaticResource Icon.AllInOne.Color}" Content="All-in-One" />
<sw:NavItem Style="{StaticResource Shotwise.NavListItem}" Icon="{StaticResource Icon.Image.Regular}" Content="Images" Badge="96" />
```

### ToggleSwitchRow / Switch
```xml
<sw:ToggleSwitchRow Content="Copy to Clipboard" IsChecked="{Binding CopyToClipboard}" />
<sw:ToggleSwitchRow Content="Delay" Description="Wait before capturing" Icon="{StaticResource Icon.Timer.Regular}"
                    IsChecked="{Binding DelayEnabled}">
    <sw:ToggleSwitchRow.Trailing><Border Style="{StaticResource Shotwise.Chip}"><TextBlock Text="5 s" /></Border></sw:ToggleSwitchRow.Trailing>
</sw:ToggleSwitchRow>
<CheckBox Style="{StaticResource Shotwise.Switch}" IsChecked="{Binding X}" AutomationProperties.Name="X" />  <!-- bare switch -->
```
Use rows (not checkboxes) for on/off preferences.

### Card, PanelHeader, PropertyRow, SliderWithValue
```xml
<sw:Card Padding="16" Elevation="1">  ...  </sw:Card>                 <!-- Elevation 0 flat, 1 card, 2 flyout; CornerRadius 10 -->
<Border Style="{StaticResource Shotwise.CardBorder}"> ... </Border>   <!-- flat card without shadow -->
<sw:PanelHeader Content="Tool Properties" HelpText="What these settings do" HelpRequested="OnHelp"
                CanCollapse="True" IsExpanded="{Binding PropsExpanded}">
    <sw:PanelHeader.Actions><ComboBox ... /></sw:PanelHeader.Actions>
</sw:PanelHeader>
<sw:PropertyRow Label="Font" Trailing="{Binding ...}"><ComboBox ... /></sw:PropertyRow>   <!-- LabelWidth 84 -->
<sw:SliderWithValue Label="Width" Minimum="1" Maximum="40" Value="{Binding Width}" />      <!-- two-way by default -->
<sw:SliderWithValue Label="Opacity" Maximum="100" Unit="%" Value="{Binding OpacityPercent}" Decimals="0" />
```
Standalone styles: `Shotwise.Slider` (Slider), `Shotwise.TextBox`, `Shotwise.NumberBox`, `Shotwise.SearchBox` (`Tag` = placeholder;
`sw:SwUi.Icon` shows a leading icon on any `Shotwise.TextBox`).
Bars: `Shotwise.TopBar`, `Shotwise.BottomBar`, `Shotwise.SidePanel` (Border styles), `Shotwise.Divider`, `Shotwise.VDivider`,
`Shotwise.Chip`, `Shotwise.AccentChip`, `Shotwise.Badge`.

### ColorSwatchButton / ShadowDirectionPicker / StylePreviewComboBox
```xml
<sw:ColorSwatchButton Label="Color" SelectedColor="{Binding StrokeColor}" ThemeColors="..." MyColors="..." />
```
Derives from the existing `ColorPicker` (flyout: theme colours, standard grid, my colours, opacity, hex, eyedropper,
"More colours…"). New, additive on `ColorPicker`: bindable `SelectedColor` (Argb, two-way) and read-only `SwatchBrush`; the flyout
now uses Surface1 + flyout elevation + `Shotwise.SwatchCell` swatches. `ThemeColors`/`MyColors` are plain CLR properties (set in code
or one-way bind via code-behind).
```xml
<sw:ShadowDirectionPicker Direction="{Binding ShadowDirection}" />
```
`ShadowDirection` = None, TopLeft … BottomRight, Around (centre). Convert with `ShadowDirectionPicker.ToAngle(dir)` /
`FromAngle(double?)` — angles follow `ShadowStyle.AngleDegrees` (0 = right, 90 = down; null = no offset).
```xml
<sw:StylePreviewComboBox ItemsSource="{Binding DashStyles}" SelectedItem="{Binding Dash}"
                         PreviewTemplate="{StaticResource Shotwise.PreviewTemplate.DashArray}" AutomationProperties.Name="Line style" />
```
Ready templates: `Shotwise.PreviewTemplate.LineWidth` (item `double`), `.DashArray` (`DoubleCollection`), `.Geometry` (`Geometry`,
e.g. arrow caps), `.Image` (`ImageSource`, e.g. a rendered preview). Any DataTemplate works.

### QuickStyleTile, TrayItem (+ lists)
```xml
<ListBox Style="{StaticResource Shotwise.TileList}" ItemsSource="{Binding QuickStyles}" SelectedItem="{Binding Selected}">
  <ListBox.ItemTemplate><DataTemplate>
    <sw:QuickStyleTile Thumbnail="{Binding Preview}" IsFavorite="{Binding IsFavorite}" Title="{Binding Name}" />
  </DataTemplate></ListBox.ItemTemplate>
</ListBox>
<ListBox Style="{StaticResource Shotwise.TrayList}" ItemsSource="{Binding Recent}">
  <ListBox.ItemTemplate><DataTemplate>
    <sw:TrayItem Thumbnail="{Binding Thumb}" Title="{Binding Name}" IsPinned="{Binding Pinned}"
                 Duration="{Binding DurationText}" IsModified="{Binding IsDirty}" />
  </DataTemplate></ListBox.ItemTemplate>
</ListBox>
```
Inside these lists `IsSelected` follows the ListBoxItem automatically; standalone, set it yourself. Render quick-style
thumbnails with `IDocumentRenderer` into an `ImageSource` (60 px tile, 4 columns fit the 312 px panel).

### InfoBar / Toast / EmptyState
```xml
<sw:InfoBar Severity="Warning" Title="Unsaved changes" Message="3 captures are not in the library yet." IsOpen="{Binding ShowWarning}">
    <sw:InfoBar.Action><Button Style="{StaticResource Shotwise.SecondaryButton}" Content="Save all" /></sw:InfoBar.Action>
</sw:InfoBar>
<sw:InfoBar Style="{StaticResource Shotwise.Toast}" Severity="Success" Title="Copied" />       <!-- floating, elevated -->
<sw:EmptyState Illustration="{StaticResource Icon.Library.Color}" Title="No captures yet" Description="...">
    <sw:EmptyState.Action><Button Style="{StaticResource Shotwise.PrimaryButton}" Content="New capture" /></sw:EmptyState.Action>
</sw:EmptyState>
```
Severity: Informational, Success, Warning, Error. Close button raises `Closed` and sets `IsOpen=false`.

## 4. Screen recipes
- **Editor top bar**: `Border Shotwise.TopBar` › [IconButton menu] [SegmentedNav] [VDivider] [ToolButtons with `Icon.Tool.*.Color`
  + `ToolCommandButton` More] [VDivider] [ToolCommandButton Undo/Redo, Regular icons] … [GhostButton Copy All] [SplitButton Share].
- **Side panel**: `Border Shotwise.SidePanel` › PanelHeader "Quick Styles" + `TileList`; Divider; PanelHeader "Tool Properties"
  (HelpText) › `UniformGrid` of ColorSwatchButtons + ShadowDirectionPicker › StylePreviewComboBoxes › SliderWithValue rows.
- **Canvas**: background `Shotwise.CanvasBrush`; image on a layer with `Shotwise.Elevation.Card`.
- **Bottom bar**: `Border Shotwise.BottomBar` › GhostToggleButtons (Recent, Effects | Properties), `TrayList`, zoom Slider.
- **Capture window**: `sw:Card CornerRadius="14"` › ModeRail (ModeRailItem, Color icons) | ToggleSwitchRows on a Surface0 inset |
  CaptureButton + reset IconButton; footer GhostButtons. Video mode: rows with `Icon.Mic/Speaker/Webcam.Regular` icons.
- **Recording HUD / widget**: pill `Border` with `Shotwise.HudBrush`, `Shotwise.HudTextBrush` text, coral `CaptureSolidBrush` dot,
  IconButtons with `Foreground="{DynamicResource Shotwise.HudTextBrush}"`.
- **Dialogs / Preferences**: Window background Surface0, content in `sw:Card`s with PanelHeaders, ToggleSwitchRows,
  footer = SecondaryButton (Cancel) + PrimaryButton (OK); Preferences left nav = NavListItems with Regular icons.

## 5. Icons
- Regular / Filled: **Geometry** in a 24-unit box → render with `sw:SwIcon` (fill = inherited Foreground, so it follows button
  states): `<sw:SwIcon Source="{StaticResource Icon.Undo.Regular}" Size="20" />`. Sizes: 16 small, 20 chrome, 24 tools.
- Color: 24×24 **DrawingImage** with gradients → `sw:SwIcon` or `<Image Source=...>`. `Icon.Brand.Color` = Shotwise app mark.
- `SwIcon.Source` also accepts a key string (`Source="Icon.Tool.Text.Color"`) for view-model driven icons.
- Tool hues (tinted Filled glyph, own palette): Arrow red-orange, Line orange, Text blue, Callout amber (Fluent Color), Shape green,
  Stamp pink, Step violet, Pen/Highlighter yellow, Fill orange, Eraser rose, Magnify sky, Blur slate-cyan, Crop teal, CutOut gold,
  Selection indigo, Magic wand fuchsia, Simplify lime, Select/Move/Cursor neutral, Grab Text + Favorites = Fluent Color.
- Regenerate / add icons: edit `tools/icon-import/icons.manifest`, run the tool (`tools/icon-import/README.md`). Licence: MIT,
  recorded in `THIRD-PARTY-NOTICES.md`.

| Key | Fluent source | Regular | Filled | Color |
|---|---|---|---|---|
| `Icon.Tool.Select.*` | `cursor` | ✓ | ✓ | Tinted `#B9BED0`→`#5A5F73` |
| `Icon.Tool.Move.*` | `arrow_move` | ✓ | ✓ | Tinted `#A3ABBE`→`#4B5468` |
| `Icon.Tool.Arrow.*` | `arrow_up_left` | ✓ | ✓ | Tinted `#FF9466`→`#EE3B2B` |
| `Icon.Tool.Line.*` | `line` | ✓ | ✓ | Tinted `#FFC27A`→`#F26B0F` |
| `Icon.Tool.Shape.*` | `shapes` | ✓ | ✓ | Tinted `#5EE08A`→`#139A45` |
| `Icon.Tool.Callout.*` | `comment` | ✓ | ✓ | Fluent Color `comment` |
| `Icon.Tool.Text.*` | `text_t` | ✓ | ✓ | Tinted `#6FB4FF`→`#2160E8` |
| `Icon.Tool.Stamp.*` | `sticker` | ✓ | ✓ | Tinted `#FF8CC8`→`#D9206F` |
| `Icon.Tool.Step.*` | `number_circle_1` | ✓ | ✓ | Tinted `#B9A2FF`→`#7434E6` |
| `Icon.Tool.Pen.*` | `pen` | ✓ | ✓ | Tinted `#FFD84D`→`#E09A00` |
| `Icon.Tool.Highlighter.*` | `highlight` | ✓ | ✓ | Tinted `#FFE866`→`#EFAE00` |
| `Icon.Tool.Fill.*` | `paint_bucket` | ✓ | ✓ | Tinted `#FFB46B`→`#E8590C` |
| `Icon.Tool.Eraser.*` | `eraser` | ✓ | ✓ | Tinted `#FF9DB2`→`#DE1D4B` |
| `Icon.Tool.Magnify.*` | `zoom_in` | ✓ | ✓ | Tinted `#7FD6FF`→`#0A84CC` |
| `Icon.Tool.Blur.*` | `blur` | ✓ | ✓ | Tinted `#86D9E4`→`#2F8295` |
| `Icon.Tool.Crop.*` | `crop` | ✓ | ✓ | Tinted `#40E2C6`→`#0C8A7D` |
| `Icon.Tool.CutOut.*` | `cut` | ✓ | ✓ | Tinted `#FFD257`→`#D27A05` |
| `Icon.Tool.Selection.*` | `select_object` | ✓ | ✓ | Tinted `#9097FF`→`#4C43E0` |
| `Icon.Tool.MagicWand.*` | `wand` | ✓ | ✓ | Tinted `#F08DFF`→`#A11BB0` |
| `Icon.Tool.Simplify.*` | `shape_union` | ✓ | ✓ | Tinted `#B8E85A`→`#4B8A0E` |
| `Icon.Tool.GrabText.*` | `scan_type` | ✓ | ✓ | Fluent Color `scan_type` |
| `Icon.Tool.Cursor.*` | `cursor_click` | ✓ | ✓ | Tinted `#CFD7E3`→`#5F6F86` |
| `Icon.Tool.Favorites.*` | `star` | ✓ | ✓ | Fluent Color `star` |
| `Icon.Capture.*` | `camera` | ✓ | ✓ | Fluent Color `camera` |
| `Icon.Video.*` | `video` | ✓ | ✓ | Fluent Color `video` |
| `Icon.AllInOne.*` | `screenshot` | ✓ | ✓ | Tinted `#A49AFF`→`#5B4CF5` |
| `Icon.Record.*` | `record` | ✓ | ✓ |  |
| `Icon.RecordStop.*` | `record_stop` | ✓ | ✓ |  |
| `Icon.ScreenshotRecord.*` | `screenshot_record` | ✓ | ✓ |  |
| `Icon.Library.*` | `library` | ✓ | ✓ | Fluent Color `library` |
| `Icon.Grid.*` | `grid` | ✓ | ✓ |  |
| `Icon.Create.*` | `add_circle` | ✓ | ✓ | Fluent Color `add_circle` |
| `Icon.Add.*` | `add` | ✓ | ✓ |  |
| `Icon.Editor.*` | `edit` | ✓ | ✓ | Fluent Color `edit` |
| `Icon.Image.*` | `image` | ✓ | ✓ | Fluent Color `image` |
| `Icon.Webcam.*` | `video_person` | ✓ | ✓ |  |
| `Icon.Mic.*` | `mic` | ✓ | ✓ | Fluent Color `mic` |
| `Icon.MicOff.*` | `mic_off` | ✓ | ✓ |  |
| `Icon.Speaker.*` | `speaker_2` | ✓ | ✓ |  |
| `Icon.SpeakerMute.*` | `speaker_mute` | ✓ | ✓ |  |
| `Icon.Scroll.*` | `arrow_autofit_height` | ✓ | ✓ |  |
| `Icon.Window.*` | `window` | ✓ | ✓ |  |
| `Icon.Desktop.*` | `desktop` | ✓ | ✓ |  |
| `Icon.Timer.*` | `timer` | ✓ | ✓ |  |
| `Icon.Undo.*` | `arrow_undo` | ✓ | ✓ |  |
| `Icon.Redo.*` | `arrow_redo` | ✓ | ✓ |  |
| `Icon.Share.*` | `share` | ✓ | ✓ |  |
| `Icon.Copy.*` | `copy` | ✓ | ✓ |  |
| `Icon.Save.*` | `save` | ✓ | ✓ |  |
| `Icon.Open.*` | `open` | ✓ | ✓ |  |
| `Icon.Folder.*` | `folder` | ✓ | ✓ |  |
| `Icon.Delete.*` | `delete` | ✓ | ✓ |  |
| `Icon.Search.*` | `search` | ✓ | ✓ |  |
| `Icon.Tag.*` | `tag` | ✓ | ✓ |  |
| `Icon.Pin.*` | `pin` | ✓ | ✓ | Fluent Color `pin` |
| `Icon.Play.*` | `play` | ✓ | ✓ |  |
| `Icon.Pause.*` | `pause` | ✓ | ✓ |  |
| `Icon.Stop.*` | `stop` | ✓ | ✓ |  |
| `Icon.Settings.*` | `settings` | ✓ | ✓ | Fluent Color `settings` |
| `Icon.Help.*` | `question_circle` | ✓ | ✓ | Fluent Color `question_circle` |
| `Icon.More.*` | `more_horizontal` | ✓ | ✓ |  |
| `Icon.MoreVertical.*` | `more_vertical` | ✓ | ✓ |  |
| `Icon.Menu.*` | `navigation` | ✓ | ✓ |  |
| `Icon.ChevronDown.*` | `chevron_down` | ✓ | ✓ |  |
| `Icon.Dismiss.*` | `dismiss` | ✓ | ✓ |  |
| `Icon.Checkmark.*` | `checkmark` | ✓ | ✓ |  |
| `Icon.Reset.*` | `arrow_reset` | ✓ | ✓ |  |
| `Icon.Eyedropper.*` | `eyedropper` | ✓ | ✓ |  |
| `Icon.Link.*` | `link` | ✓ | ✓ | Fluent Color `link` |
| `Icon.History.*` | `history` | ✓ | ✓ | Fluent Color `history` |
| `Icon.Print.*` | `print` | ✓ | ✓ |  |
| `Icon.Mail.*` | `mail` | ✓ | ✓ |  |
| `Icon.Cloud.*` | `cloud` | ✓ | ✓ |  |
| `Icon.Download.*` | `arrow_download` | ✓ | ✓ |  |
| `Icon.Export.*` | `arrow_export_ltr` | ✓ | ✓ |  |
| `Icon.ZoomOut.*` | `zoom_out` | ✓ | ✓ |  |
| `Icon.FitScreen.*` | `scale_fit` | ✓ | ✓ |  |
| `Icon.FullScreen.*` | `full_screen_maximize` | ✓ | ✓ |  |
| `Icon.Effects.*` | `sparkle` | ✓ | ✓ |  |
| `Icon.Properties.*` | `panel_right` | ✓ | ✓ |  |
| `Icon.Color.*` | `color` | ✓ | ✓ |  |
| `Icon.Shadow.*` | `image_shadow` | ✓ | ✓ |  |
| `Icon.Resize.*` | `resize` | ✓ | ✓ |  |
| `Icon.Gif.*` | `gif` | ✓ | ✓ |  |
| `Icon.Document.*` | `document` | ✓ | ✓ | Fluent Color `document` |
| `Icon.Filter.*` | `filter` | ✓ | ✓ |  |
| `Icon.Home.*` | `home` | ✓ | ✓ |  |
| `Icon.Person.*` | `person` | ✓ | ✓ |  |
| `Icon.Apps.*` | `apps` | ✓ | ✓ | Fluent Color `apps` |
| `Icon.Calendar.*` | `calendar` | ✓ | ✓ |  |
| `Icon.Globe.*` | `globe` | ✓ | ✓ |  |
| `Icon.Keyboard.*` | `keyboard` | ✓ | ✓ |  |
| `Icon.Lightbulb.*` | `lightbulb` | ✓ | ✓ | Fluent Color `lightbulb` |
| `Icon.Collections.*` | `collections` | ✓ | ✓ |  |
| `Icon.VideoClip.*` | `video_clip` | ✓ | ✓ |  |
| `Icon.Info.*` | `info` | ✓ | ✓ |  |
| `Icon.Success.*` | `checkmark_circle` | ✓ | ✓ | Fluent Color `checkmark_circle` |
| `Icon.Warning.*` | `warning` | ✓ | ✓ | Fluent Color `warning` |
| `Icon.Error.*` | `error_circle` | ✓ | ✓ | Fluent Color `error_circle` |
| `Icon.Brand.Color` / `.Filled` | Shotwise original | | ✓ | app mark = the app icon (POLISH-1, `tools/icon-gen`); for 16–24 px chrome use `AppIcon.ForSize(dips, dpiScale)` (pixel-hinted .ico frames) |

## 6. Do / Don't
**Do**
- Use `{DynamicResource Shotwise.*Brush}` for every colour; `StaticResource` only for styles, metrics, icons and templates.
- Colour where it carries meaning: tool icons, capture/record, status, the one primary action per surface. Everything else neutral.
- One `PrimaryButton` (or accent SplitButton) per window region; secondary actions = Secondary/Ghost.
- Chrome icons: Regular, 16–20 px, TextSecondary (they inherit Foreground). Tool/mode icons: Color, 24–28 px.
- Group content in `sw:Card`s on Surface0 windows; use PanelHeaders for sections; 4/8/12/16/24 spacing.
- Give every icon-only control `AutomationProperties.Name` + `ToolTip` with the shortcut; keep keyboard focus visible.
- Check both themes (render with `StyleGalleryRenderer.Render(element, path)` or the smoke harness).

**Don't**
- Hard-code hex colours, `Brushes.*` or WPF-UI theme brushes in screen XAML (WPF-UI's own controls are fine — their accent is
  already the brand accent).
- Put `DropShadowEffect` on containers with text, or stack several elevated layers inside each other.
- Use outlined boxes around every tool (the old look), lavender-on-grey selection, or text tabs where a panel mode switch fits.
- Mix Regular and Filled glyphs in the same toolbar; don't recolour Color icons.
- Copy another product's icons, artwork, colours or layout.

## 7. Notes for integrators
- `GeneralSettings.Theme` defaults to `AppTheme.Light`; Dark and System stay selectable in Preferences.
- Attached-property class is `SwUi` (not `Ui`) to avoid clashing with `Shotwise.UI.Capture.Infrastructure.Ui`.
- Type names that also exist in WPF-UI (`Card`, `InfoBar`, `SplitButton`): in C# files importing both `Wpf.Ui.Controls` and
  `Shotwise.UI.Common.Controls`, qualify them or alias (`using SwCard = Shotwise.UI.Common.Controls.Card;`).
- Tool harnesses that build an `Application` in code: call `StyleGallery.EnsureResources()` (assigns `Application.Resources` through the
  setter — merging into the lazily created default dictionary does not reach BAML `StaticResource` lookups reliably).
