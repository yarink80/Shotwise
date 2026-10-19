using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Shotwise.UI.Common.Controls;
using Shotwise.UI.Editor.Infrastructure;

namespace Shotwise.UI.Editor.Panels;

/// <summary>
/// Start page / learning center (EDT-16): welcome header, colourful quick-action cards, then our own getting-started text,
/// tutorials-in-brief, the shortcut cheat sheet and what's new behind a segmented switch. Shown when no document is open or from Help.
/// </summary>
internal sealed class LearningCenterView : Grid
{
    private readonly List<(NavItem Nav, FrameworkElement Page)> _sections = new();

    public LearningCenterView(Action newCapture, Action newImage, Action open, Action library, Action<bool> dontShowAgain, bool showOnStartup)
    {
        SetResourceReference(BackgroundProperty, Tok.Surface0);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        var root = new StackPanel { Margin = new Thickness(40, 32, 40, 32), MaxWidth = 960, HorizontalAlignment = HorizontalAlignment.Center };

        var hero = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 24) };
        hero.Children.Add(new SwIcon { Source = "Icon.Brand.Color", Size = 56, Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center });
        var heroText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        heroText.Children.Add(new TextBlock { Text = ES.T("Learn_Welcome", "Welcome to Shotwise") }.Styled("Shotwise.Text.Display"));
        heroText.Children.Add(new TextBlock { Text = ES.T("Learn_Tagline", "Capture anything on your screen, mark it up, and share it in seconds."), FontSize = 15, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap }.Themed(TextBlock.ForegroundProperty, Tok.Text2));
        hero.Children.Add(heroText);
        root.Children.Add(hero);

        var actions = new UniformGrid { Columns = 4, Margin = new Thickness(-6, 0, -6, 28) };
        actions.Children.Add(ActionCard("Icon.Capture.Color", ES.T("Learn_NewCapture", "New capture"), ES.T("Learn_NewCaptureSub", "Image, video or scrolling"), newCapture, true));
        actions.Children.Add(ActionCard("Icon.Image.Color", ES.T("Learn_NewImage", "New image…"), ES.T("Learn_NewImageSub", "Blank canvas or clipboard"), newImage, false));
        actions.Children.Add(ActionCard("Icon.Document.Color", ES.T("Learn_Open", "Open file…"), ES.T("Learn_OpenSub", "Images, videos, .shotx"), open, false));
        actions.Children.Add(ActionCard("Icon.Library.Color", ES.T("Learn_Library", "Browse library"), ES.T("Learn_LibrarySub", "Search every capture"), library, false));
        root.Children.Add(actions);

        var navPanel = new StackPanel { Orientation = Orientation.Horizontal };
        var nav = new Border { Child = navPanel, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 14) }.Styled("Shotwise.SegmentedNav");
        root.Children.Add(nav);
        var pages = new Grid();
        root.Children.Add(pages);
        AddSection(navPanel, pages, "Icon.Lightbulb.Regular", ES.T("Learn_GettingStarted", "Getting started"), new[]
        {
            (ES.T("Learn_Step1T", "1. Capture"), ES.T("Learn_Step1", "Press the capture hotkey (PrintScreen by default) or choose New Capture. Drag a region, click a window, or pick the full screen, then choose image, video or scrolling capture.")),
            (ES.T("Learn_Step2T", "2. Mark it up"), ES.T("Learn_Step2", "Pick a tool on the toolbar. Arrows, callouts, text, steps, stamps, blur and more stay editable until you flatten or export. Use Quick Styles on the right to reuse your favourite looks.")),
            (ES.T("Learn_Step3T", "3. Polish"), ES.T("Learn_Step3", "Open the Effects panel to add borders, shadows, torn edges, colour changes and more. Save combinations as Quick Effects.")),
            (ES.T("Learn_Step4T", "4. Share"), ES.T("Learn_Step4", "Use the Share button to save, copy, print, email or upload to any destination you have set up in Preferences.")),
            (ES.T("Learn_Step5T", "5. Find it again"), ES.T("Learn_Step5", "Every capture is kept in your library. Search by what is in the picture, filter by application, website, date or tag, and pin your favourites to the tray.")),
        });
        AddSection(navPanel, pages, "Icon.VideoClip.Regular", ES.T("Learn_Tutorials", "Tutorials"), new[]
        {
            (ES.T("Learn_T1T", "Hide sensitive information"), ES.T("Learn_T1", "Choose Image → Smart Redact. Review the categories found (faces, emails, numbers…), pick blur, pixelate or solid, then press Done.")),
            (ES.T("Learn_T2T", "Make a step-by-step guide"), ES.T("Learn_T2", "Use the Step capture preset, click through your task, and the editor opens a guide with numbered steps. Combine screenshots with File → Create Image from Template.")),
            (ES.T("Learn_T3T", "Trim a recording"), ES.T("Learn_T3", "Open a video, drag across the timeline to select a range, then Cut Out removes it or Trim keeps only it. Every change can be undone.")),
            (ES.T("Learn_T4T", "Turn a video into a GIF"), ES.T("Learn_T4", "In the video view choose Create GIF, pick a preset (screen video, high motion, small file) and optional fade-out.")),
            (ES.T("Learn_T5T", "Move things around in a screenshot"), ES.T("Learn_T5", "Choose the Move tool and turn on Smart Move. Buttons and panels become objects you can move, resize or delete.")),
            (ES.T("Learn_T6T", "Copy text out of an image"), ES.T("Learn_T6", "Choose Edit → Grab Text, or use the Grab Text tool to drag over a region. Copy the result as text or as a table.")),
        });
        AddSection(navPanel, pages, "Icon.Keyboard.Regular", ES.T("Learn_Shortcuts", "Keyboard shortcuts"), new[]
        {
            (ES.T("Learn_KeyFile", "File"), "Ctrl+N New image · Ctrl+Shift+N New from clipboard · Ctrl+O Open · Ctrl+S Save · Ctrl+Shift+S Save as · Ctrl+P Print · Ctrl+Alt+T Create from template · Ctrl+F4 Close · Ctrl+Del Move to Recycle Bin · F2 Rename · Ctrl+Shift+P Pin"),
            (ES.T("Learn_KeyEdit", "Edit"), "Ctrl+Z Undo · Ctrl+Y Redo · Ctrl+X/C/V Cut, copy, paste · Ctrl+Shift+C Copy all · Ctrl+A Select all · Ctrl+D Deselect · Ctrl+Shift+I Invert selection · Ctrl+Shift+G Group · Ctrl+Shift+U Ungroup · Ctrl+Shift+F/B Bring forward / Send backward · Ctrl+T Flatten · Ctrl+Shift+T Flatten all"),
            (ES.T("Learn_KeyView", "View and tools"), "Ctrl++ / Ctrl+- Zoom · Ctrl+0 Actual size · Ctrl+G Pixel grid · Ctrl+; Canvas snapping · Ctrl+1…9 Tools · Space + drag, or middle-drag Pan · Ctrl+Alt+C Combine images · Ctrl+Shift+V Combine videos · F10 Menu"),
            (ES.T("Learn_KeyVideo", "Video"), "Space Play / pause · , and . Previous / next frame · Home / End Go to start / end"),
        });
        AddSection(navPanel, pages, "Icon.Effects.Regular", ES.T("Learn_WhatsNew", "What's new"), new[]
        {
            (ES.T("Learn_N1T", "One window for everything"), ES.T("Learn_N1", "Images, templates, videos and GIFs open side by side in the tray. Switch between them with a click.")),
            (ES.T("Learn_N2T", "Smart tools"), ES.T("Learn_N2", "Smart Redact, Smart Move, Auto Simplify, Grab Text and Edit Text work on your own computer. Models download only when you choose to use them.")),
            (ES.T("Learn_N3T", "Your data, your rules"), ES.T("Learn_N3", "The library is a plain folder you choose, backups are a single file, and nothing is sent anywhere unless you share it.")),
        });
        ShowTab(0);

        var dont = UiKit.Check(ES.T("Learn_ShowOnStartup", "Show this page when no image is open"), showOnStartup, dontShowAgain);
        dont.Margin = new Thickness(-8, 18, 0, 0);
        dont.HorizontalAlignment = HorizontalAlignment.Left;
        dont.MinWidth = 360;
        root.Children.Add(dont);
        scroll.Content = root;
        Children.Add(scroll);
    }

    private static FrameworkElement ActionCard(string icon, string title, string subtitle, Action click, bool hero)
    {
        var body = new StackPanel { Margin = new Thickness(4, 6, 4, 4) };
        var disc = new Border { Width = 48, Height = 48, CornerRadius = new CornerRadius(14), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 14) };
        disc.SetResourceReference(Border.BackgroundProperty, hero ? "Shotwise.CaptureRingBrush" : Tok.AccentSubtle);
        disc.Child = new SwIcon { Source = icon, Size = 28, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        body.Children.Add(disc);
        body.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold }.Themed(TextBlock.ForegroundProperty, Tok.Text));
        body.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis }.Themed(TextBlock.ForegroundProperty, Tok.Text2));
        var b = new Button { Content = body, Padding = new Thickness(14, 12, 14, 12), HorizontalContentAlignment = HorizontalAlignment.Left }.Styled("Shotwise.SecondaryButton");
        SwUi.SetCornerRadius(b, new CornerRadius(12));
        b.HorizontalAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(b, title);
        b.Click += (_, _) => click();
        return new Border { Child = b, Margin = new Thickness(6) };
    }

    private void AddSection(Panel nav, Grid pages, string icon, string header, IEnumerable<(string Title, string Body)> entries)
    {
        var card = new Card { Padding = new Thickness(24, 16, 24, 20), Elevation = 1 };
        var sp = new StackPanel();
        foreach (var (title, body) in entries)
        {
            sp.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 10, 0, 3) }.Themed(TextBlock.ForegroundProperty, Tok.Text));
            sp.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 20 }.Themed(TextBlock.ForegroundProperty, Tok.Text2));
        }
        card.Content = sp;
        pages.Children.Add(card);
        var index = _sections.Count;
        var item = new NavItem { Icon = icon, Content = header, GroupName = "LearnTabs", MinWidth = 120 };
        item.Click += (_, _) => ShowTab(index);
        nav.Children.Add(item);
        _sections.Add((item, card));
    }

    public void ShowTab(int index)
    {
        index = Math.Clamp(index, 0, _sections.Count - 1);
        for (var i = 0; i < _sections.Count; i++)
        {
            _sections[i].Nav.IsChecked = i == index;
            _sections[i].Page.Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
