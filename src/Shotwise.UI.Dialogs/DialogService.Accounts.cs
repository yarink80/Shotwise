using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Shotwise.Core.Capture;
using Shotwise.Core.Settings;
using Shotwise.Core.Sharing;
using Shotwise.UI.Common.Dialogs;
using Shotwise.UI.Common.Controls;
using Button = System.Windows.Controls.Button;

namespace Shotwise.UI.Dialogs;

/// <summary>How a destination is connected (decides which fields the Share Accounts dialog shows).</summary>
internal enum AccountKind
{
    /// <summary>OAuth in the browser with the user's own client id (and optional secret / fixed loopback port).</summary>
    OAuth,
    /// <summary>The destination asks for its token itself when signing in (Slack).</summary>
    Token,
    /// <summary>Server / key / program form shown by the destination's options (FTP, SFTP, S3, custom uploader, ...).</summary>
    OptionsOnly,
    /// <summary>Listed but cannot work.</summary>
    Unavailable,
}

internal static class AccountKinds
{
    private static readonly HashSet<string> OAuthIds = new(StringComparer.OrdinalIgnoreCase)
    {
        KnownDestinations.OneDrive, KnownDestinations.SharePoint, KnownDestinations.Teams, KnownDestinations.GoogleDrive, KnownDestinations.YouTube,
        KnownDestinations.Dropbox, KnownDestinations.Box, KnownDestinations.Imgur, KnownDestinations.X,
    };

    public static AccountKind Classify(string destinationId) => destinationId switch
    {
        KnownDestinations.Slack => AccountKind.Token,
        KnownDestinations.Evernote => AccountKind.Unavailable,
        _ when OAuthIds.Contains(destinationId) => AccountKind.OAuth,
        _ => AccountKind.OptionsOnly,
    };

    /// <summary>Destinations that appear in the Accounts dialog, OAuth providers first, then token, then options-only services.</summary>
    public static IReadOnlyList<IShareDestination> Listed(IEnumerable<IShareDestination> all)
        => all.Where(d => d.RequiresAccount || OAuthIds.Contains(d.Id))
              .OrderBy(d => (int)Classify(d.Id)).ThenBy(d => d.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Settings key shared by destinations of one provider (Microsoft Graph, Google).</summary>
    public static string ProviderId(string destination) => destination switch
    {
        KnownDestinations.OneDrive or KnownDestinations.SharePoint or KnownDestinations.Teams => "microsoft",
        KnownDestinations.GoogleDrive or KnownDestinations.YouTube => "google",
        _ => destination,
    };
}

public sealed partial class DialogService
{
    public async Task ShowShareAccounts(ShareAccountsRequest r)
    {
        var w = new FormWindow("ShareAccounts");
        var all = AccountKinds.Listed(Need<IShareService>().Destinations);
        w.Body.Width = 820;
        var layout = new DockPanel(); var destinations = new StackPanel { Width = 210, Margin = new Thickness(0, 0, 20, 0) };
        DockPanel.SetDock(destinations, Dock.Left); layout.Children.Add(destinations);
        var detail = new StackPanel { Width = 558 };
        layout.Children.Add(new Card { Content = detail, Padding = new Thickness(16) });
        var choice = new ComboBox { ItemsSource = all, SelectedItem = all.FirstOrDefault(d => d.Id == r.DestinationId) ?? all.FirstOrDefault(), Visibility = Visibility.Collapsed };
        w.Body.Children.Add(choice);
        var entries = new Dictionary<string, NavItem>();
        foreach (var destination in all)
        {
            var label = new StackPanel(); label.Children.Add(new TextBlock { Text = destination.DisplayName });
            label.Children.Add(DialogVisuals.Pill(new TextBlock { Text = Texts.Get(destination.IsConfigured ? "Configured" : "NotConfigured"), FontSize = 11 }, destination.IsConfigured));
            var entry = new NavItem { Content = label, Icon = "Icon.Cloud.Regular", GroupName = "Destinations", Height = 64, Margin = new Thickness(0, 0, 0, 8) };
            entry.SetResourceReference(FrameworkElement.StyleProperty, "Shotwise.NavListItem");
            entry.Checked += (_, _) => choice.SelectedItem = destination;
            destinations.Children.Add(entry); entries[destination.Id] = entry;
        }
        var description = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(0, 0, 0, 8) }; w.Body.Children.Add(description);

        // OAuth fields: only meaningful for browser-sign-in providers.
        var oauth = new StackPanel();
        var client = new TextBox { MinHeight = 28 }; var secret = new PasswordBox();
        oauth.Children.Add(FormWindow.Labelled("ClientId", client));
        oauth.Children.Add(FormWindow.Labelled("ClientSecret", secret));
        oauth.Children.Add(new TextBlock { Text = Texts.Get("P_ClientSecretHint"), TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12, Margin = new Thickness(0, -4, 0, 8) });
        var port = new TextBox { MinHeight = 28 }; var portBox = FormWindow.Labelled("LoopbackPort", port); oauth.Children.Add(portBox);
        var portHint = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12, Margin = new Thickness(0, -4, 0, 8) }; oauth.Children.Add(portHint);
        w.Body.Children.Add(oauth);

        var status = new TextBlock { Margin = new Thickness(0, 4, 0, 8), FontWeight = FontWeights.SemiBold }; w.Body.Children.Add(DialogVisuals.Pill(status));
        var buttons = new WrapPanel(); w.Body.Children.Add(buttons);
        IShareDestination? shown = null; bool reverting = false;
        IShareDestination Current() => choice.SelectedItem as IShareDestination ?? throw new ArgumentException(Texts.Get("ChooseDestination"));
        Button signIn = null!, signOut = null!, options = null!;
        signIn = w.InlineAction("SignIn", async () => { Save(); await Current().SignInAsync(this, w.Lifetime.Token); Load(); }, buttons);
        signOut = w.InlineAction("SignOut", async () => { await Current().SignOutAsync(); Load(); }, buttons);
        options = w.InlineAction("P_Options", async () => { Save(); await Current().ShowOptionsAsync(this); Load(); }, buttons);

        void Load()
        {
            if (choice.SelectedItem is not IShareDestination d) { description.Text = Texts.Get("ChooseDestination"); oauth.Visibility = signIn.Visibility = signOut.Visibility = options.Visibility = Visibility.Collapsed; return; }
            shown = d; entries[d.Id].IsChecked = true;
            if (entries[d.Id].Content is StackPanel labels && labels.Children[1] is Border { Child: TextBlock badge } pill)
            {
                badge.Text = Texts.Get(d.IsConfigured ? "Configured" : "NotConfigured");
                badge.SetResourceReference(TextBlock.ForegroundProperty, d.IsConfigured ? "Shotwise.SuccessTextBrush" : "Shotwise.AccentTextBrush");
                pill.SetResourceReference(Border.BackgroundProperty, d.IsConfigured ? "Shotwise.SuccessSubtleBrush" : "Shotwise.AccentSubtleBrush");
            }
            var kind = AccountKinds.Classify(d.Id); var provider = AccountKinds.ProviderId(d.Id);
            oauth.Visibility = kind == AccountKind.OAuth ? Visibility.Visible : Visibility.Collapsed;
            signIn.Visibility = signOut.Visibility = kind is AccountKind.OAuth or AccountKind.Token ? Visibility.Visible : Visibility.Collapsed;
            options.Visibility = d.CanShowOptions && kind != AccountKind.Unavailable ? Visibility.Visible : Visibility.Collapsed;
            description.Text = kind switch
            {
                AccountKind.OAuth => Texts.Format("P_AccountOAuth", d.DisplayName),
                AccountKind.Token => Texts.Format("P_AccountToken", d.DisplayName),
                AccountKind.OptionsOnly => Texts.Format("P_AccountOptions", d.DisplayName),
                _ => Texts.Format("P_AccountUnavailable", d.DisplayName),
            };
            options.Content = Texts.Get(kind == AccountKind.OptionsOnly ? "P_ConfigureDestination" : "P_Options");
            if (kind == AccountKind.OAuth)
            {
                client.Text = settings.Current.Share.ClientIds.GetValueOrDefault(provider, ""); secret.Password = "";
                bool fixedPort = ShareCredentialKeys.FixedPortProviders.Contains(provider);
                portBox.Visibility = portHint.Visibility = fixedPort ? Visibility.Visible : Visibility.Collapsed;
                port.Text = settings.Current.Share.LoopbackPorts.TryGetValue(provider, out var p) ? p.ToString(CultureInfo.InvariantCulture)
                    : fixedPort ? ShareCredentialKeys.DefaultLoopbackPort.ToString(CultureInfo.InvariantCulture) : "";
                portHint.Text = fixedPort ? string.Format(CultureInfo.CurrentCulture, Texts.Get("LoopbackPortHint"), port.Text) : "";
            }
            status.Text = kind == AccountKind.Unavailable ? "" : Texts.Get(d.IsConfigured ? "Configured" : "NotConfigured");
            signOut.IsEnabled = d.IsConfigured;
        }
        void Save()
        {
            if (shown is not { } d || AccountKinds.Classify(d.Id) != AccountKind.OAuth) return;
            var provider = AccountKinds.ProviderId(d.Id);
            int? fixedPort = null;
            if (ShareCredentialKeys.FixedPortProviders.Contains(provider) && !string.IsNullOrWhiteSpace(port.Text))
            {
                if (!int.TryParse(port.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n is < 1024 or > 65535)
                    throw new ArgumentException(Texts.Get("LoopbackPort") + ": 1024 – 65535");
                fixedPort = n;
            }
            settings.Update(s =>
            {
                s.Share.ClientIds[provider] = client.Text.Trim();
                if (fixedPort is { } fp) s.Share.LoopbackPorts[provider] = fp; else s.Share.LoopbackPorts.Remove(provider);
            });
            if (!string.IsNullOrEmpty(secret.Password)) Need<ICredentialStore>().Write(ShareCredentialKeys.ClientSecret(provider), secret.Password);
            secret.Clear();
        }
        choice.SelectionChanged += (_, _) =>
        {
            if (reverting) return;
            try { Save(); }
            catch (ArgumentException ex) { w.Model.Error = ex.Message; reverting = true; choice.SelectedItem = shown; reverting = false; return; }
            w.Model.Error = null; Load();
        };
        // Keep the existing field/event objects while arranging them in the detail card.
        foreach (var child in w.Body.Children.Cast<UIElement>().Where(e => e != choice).ToArray()) { w.Body.Children.Remove(child); detail.Children.Add(child); }
        w.Body.Children.Add(layout);
        Load();
        w.Commit = () => { Save(); return Task.CompletedTask; };
        await w.ShowDialogAsync(r.Owner);
    }

    public async Task ShowDestinationOptions(DestinationOptionsRequest r)
    {
        var destination = Need<IShareService>().Destinations.FirstOrDefault(d => d.Id == r.DestinationId) ?? throw new ArgumentException(Texts.Get("ChooseDestination"));
        if (!destination.CanShowOptions) { await ShowMessage(new MessageRequest(destination.DisplayName, Texts.Get("P_NoDestinationOptions")) { Owner = r.Owner }); return; }
        await destination.ShowOptionsAsync(this);
    }
}
