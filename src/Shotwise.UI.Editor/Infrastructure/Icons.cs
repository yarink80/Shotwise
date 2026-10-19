using System.Windows;
using System.Windows.Media;
using Shotwise.Core.Styles;
using Shotwise.UI.Common.Controls;

namespace Shotwise.UI.Editor.Infrastructure;

/// <summary>
/// Icon lookup for the editor: every icon is a Shotwise design-system resource (docs/DESIGN-SYSTEM.md §5 — Fluent UI System
/// Icons, MIT, converted by D1). Tools use their colour variant (<c>Icon.Tool.X.Color</c>); chrome uses Regular glyphs that follow
/// the inherited foreground.
/// </summary>
internal static class Icons
{
    /// <summary>Resource key of a tool's colour icon (24 px, gradient).</summary>
    public static string ToolKey(ToolKind kind, string variant = "Color") => $"Icon.Tool.{kind}.{variant}";

    /// <summary>Colour tool icon element (used in menus, the customise dialog, tool lists).</summary>
    public static FrameworkElement ForTool(ToolKind kind, double size = 20)
        => new SwIcon { Source = ToolKey(kind), Size = size, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>
    /// Chrome glyph keys (Regular, 24-unit geometry). A suffix <c>@deg</c> rotates the glyph (chevrons for previous/next/up).
    /// Resolve with <see cref="UiKit.IconSource"/> / <see cref="UiKit.Glyph"/>.
    /// </summary>
    public static class Glyph
    {
        public const string Add = "Icon.Add.Regular", Cancel = "Icon.Dismiss.Regular", More = "Icon.More.Regular", Settings = "Icon.Settings.Regular",
            Edit = "Icon.Editor.Regular", Delete = "Icon.Delete.Regular", Save = "Icon.Save.Regular", Copy = "Icon.Copy.Regular",
            Cut = "Icon.Tool.CutOut.Regular", Paste = "Icon.Document.Regular", Undo = "Icon.Undo.Regular", Redo = "Icon.Redo.Regular",
            Search = "Icon.Search.Regular", Filter = "Icon.Filter.Regular", Star = "Icon.Tool.Favorites.Regular", StarFill = "Icon.Tool.Favorites.Filled",
            Pin = "Icon.Pin.Regular", PinFill = "Icon.Pin.Filled", Accept = "Icon.Checkmark.Regular", Play = "Icon.Play.Filled", Pause = "Icon.Pause.Filled",
            Stop = "Icon.Stop.Filled", Refresh = "Icon.Reset.Regular", Share = "Icon.Share.Regular", Mute = "Icon.SpeakerMute.Regular", Volume = "Icon.Speaker.Regular",
            Previous = "Icon.ChevronDown.Regular@90", Next = "Icon.ChevronDown.Regular@270", Folder = "Icon.Folder.Regular", Page = "Icon.Document.Regular",
            ZoomIn = "Icon.Tool.Magnify.Regular", ZoomOut = "Icon.ZoomOut.Regular", Tag = "Icon.Tag.Regular", Back = "Icon.ChevronDown.Regular@90",
            Forward = "Icon.ChevronDown.Regular@270", Camera = "Icon.Capture.Regular", Library = "Icon.Library.Regular", Photo = "Icon.Image.Regular",
            Link = "Icon.Link.Regular", Up = "Icon.ChevronDown.Regular@180", Down = "Icon.ChevronDown.Regular", Print = "Icon.Print.Regular",
            OpenFile = "Icon.Open.Regular", Image = "Icon.Image.Regular", Video = "Icon.Video.Regular", Info = "Icon.Info.Regular", Help = "Icon.Help.Regular",
            Fit = "Icon.FitScreen.Regular", Layers = "Icon.Collections.Regular", Effects = "Icon.Effects.Regular", Properties = "Icon.Properties.Regular",
            Brush = "Icon.Tool.Pen.Regular", Close = "Icon.Dismiss.Regular", Rotate = "Icon.Reset.Regular", Flip = "Icon.Resize.Regular",
            Menu = "Icon.Menu.Regular", History = "Icon.History.Regular", Export = "Icon.Export.Regular", Mail = "Icon.Mail.Regular", Cloud = "Icon.Cloud.Regular",
            Globe = "Icon.Globe.Regular", Apps = "Icon.Apps.Regular", Person = "Icon.Person.Regular", Calendar = "Icon.Calendar.Regular", Grid = "Icon.Grid.Regular",
            Gif = "Icon.Gif.Regular", VideoClip = "Icon.VideoClip.Regular", Keyboard = "Icon.Keyboard.Regular", Lightbulb = "Icon.Lightbulb.Regular",
            Color = "Icon.Color.Regular", Shadow = "Icon.Shadow.Regular", Resize = "Icon.Resize.Regular", Download = "Icon.Download.Regular",
            FullScreen = "Icon.FullScreen.Regular", Home = "Icon.Home.Regular", Collections = "Icon.Collections.Regular", Create = "Icon.Create.Regular",
            Eyedropper = "Icon.Eyedropper.Regular", Timer = "Icon.Timer.Regular", Window = "Icon.Window.Regular", Mic = "Icon.Mic.Regular";
    }

    /// <summary>Share destination → icon key (Regular) by id, falling back to the destination category.</summary>
    public static string ForDestination(string id, Shotwise.Core.Sharing.DestinationCategory category) => id switch
    {
        "file" => Glyph.Save,
        "clipboard" => Glyph.Copy,
        "print" => Glyph.Print,
        "email" => Glyph.Mail,
        "program" => Glyph.Apps,
        "youtube" => Glyph.Video,
        "teams" => Glyph.Person,
        "ftp" or "sftp" or "s3" => Glyph.Globe,
        "custom" => Glyph.Export,
        "word" or "excel" or "powerpoint" => Glyph.Page,
        _ => category switch
        {
            Shotwise.Core.Sharing.DestinationCategory.Storage or Shotwise.Core.Sharing.DestinationCategory.Microsoft => Glyph.Cloud,
            Shotwise.Core.Sharing.DestinationCategory.Social or Shotwise.Core.Sharing.DestinationCategory.Uploader => Glyph.Link,
            _ => Glyph.Share,
        },
    };

    /// <summary>Category tint for destination icons: (background token, foreground token).</summary>
    public static (string Bg, string Fg) DestinationTint(Shotwise.Core.Sharing.DestinationCategory category) => category switch
    {
        Shotwise.Core.Sharing.DestinationCategory.BuiltIn => ("Shotwise.AccentSubtleBrush", "Shotwise.AccentTextBrush"),
        Shotwise.Core.Sharing.DestinationCategory.Microsoft => ("Shotwise.InfoSubtleBrush", "Shotwise.AccentTextBrush"),
        Shotwise.Core.Sharing.DestinationCategory.Storage => ("Shotwise.SuccessSubtleBrush", "Shotwise.SuccessTextBrush"),
        Shotwise.Core.Sharing.DestinationCategory.Social => ("Shotwise.DangerSubtleBrush", "Shotwise.DangerTextBrush"),
        Shotwise.Core.Sharing.DestinationCategory.Uploader => ("Shotwise.WarningSubtleBrush", "Shotwise.WarningTextBrush"),
        _ => ("Shotwise.Surface2Brush", "Shotwise.TextSecondaryBrush"),
    };

    /// <summary>Rounded tinted tile with a glyph (destination icons in the Share menu, effect cards).</summary>
    public static FrameworkElement Tile(string glyphKey, string bgToken, string fgToken, double size = 24, double iconSize = 15)
    {
        var icon = UiKit.Glyph(glyphKey, iconSize);
        icon.SetResourceReference(SwIcon.ForegroundProperty, fgToken);
        var b = new System.Windows.Controls.Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 4), Child = icon, IsHitTestVisible = false };
        b.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, bgToken);
        return b;
    }
}
