using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Capture;
using Shotwise.Core.Documents;
using Shotwise.Core.Platform;
using Shotwise.Core.Settings;
using Shotwise.Platform;
using Shotwise.UI.Capture.Coordinator;
using Shotwise.UI.Common.Controls;
using Shotwise.UI.Common.Dialogs;
using Shotwise.UI.Common.Resources;
using Shotwise.UI.Common.Theming;

namespace Shotwise.App.Tray;

/// <summary>
/// Notification-area icon (SPEC CAP-18): Capture, Open Editor, Open Capture Window, Widget toggle, Preferences, Exit.
/// Uses the Shotwise tray icon (coral recording variant while a recording runs) and shows the effective capture hotkey
/// (e.g. Shift+PrintScreen when PrintScreen is taken) in the tooltip and next to the menu items.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly IServiceProvider _services;
    private readonly ILogger<TrayIconManager> _log;
    private TaskbarIcon? _icon;
    private MenuItem? _widgetItem;
    private MenuItem? _captureItem;
    private MenuItem? _captureWindowItem;
    private System.Drawing.Icon? _normalIcon, _recordingIcon;
    private bool _recording;
    private IHotkeyStatusSource? _status;
    private CaptureCoordinator? _coordinator;
    private ContextMenu? _menu;
    private MenuItem? _updateItem;
    private Separator? _updateSeparator;
    private IUpdateService? _updates;
    private TrayIconRecovery? _recovery;
    private System.Windows.Interop.HwndSource? _shellListener;

    public TrayIconManager(IServiceProvider services, ILogger<TrayIconManager> log)
    {
        _services = services;
        _log = log;
    }

    /// <summary>Creates the TaskbarIcon (tests/diagnostics can replace it, e.g. with a factory that throws).</summary>
    internal Func<TaskbarIcon> IconFactory { get; set; } = () => new TaskbarIcon();

    /// <summary>True once the notification-area icon exists.</summary>
    public bool IsCreated => _icon is { IsCreated: true };

    /// <summary>True while the tray shows the recording state.</summary>
    public bool ShowsRecording => _recording;

    /// <summary>Why the icon is missing (null when created): shown by the smoke run.</summary>
    public string? CreationError => IsCreated ? null : _recovery?.LastError?.Message ?? (_recovery is null ? "not shown yet" : "not created");

    /// <summary>
    /// Builds the menu and creates the icon. Never throws: without a notification area (no interactive desktop, Explorer not
    /// running) Shotwise keeps running with its windows and creates the icon when Explorer broadcasts <c>TaskbarCreated</c>.
    /// </summary>
    public void Show()
    {
        if (_menu is not null) return;
        try { BuildMenu(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Windows.Markup.XamlParseException or System.IO.IOException)
        {
            _log.LogWarning(ex, "Tray menu could not be built; the notification-area icon is unavailable");
            return;
        }
        _recovery = new TrayIconRecovery(CreateIcon, () => IsCreated, _log);
        if (_recovery.TryCreate()) _log.LogDebug("Tray icon created");
        StartShellListener();
    }

    private void BuildMenu()
    {
        var settings = _services.GetRequiredService<ISettingsService>();

        var menu = new ContextMenu();
        _captureItem = Item(Strings.Tray_Capture, "Icon.Capture.Regular", OnCapture);
        menu.Items.Add(_captureItem);
        menu.Items.Add(Item(Strings.Tray_OpenEditor, "Icon.Editor.Regular", OnOpenEditor));
        _captureWindowItem = Item(Strings.Tray_OpenCaptureWindow, "Icon.Window.Regular", OnOpenCaptureWindow);
        menu.Items.Add(_captureWindowItem);
        _widgetItem = Item(Strings.Tray_ShowWidget, null, OnToggleWidget);
        _widgetItem.IsCheckable = true;
        _widgetItem.IsChecked = settings.Current.General.ShowCaptureWidget;
        menu.Items.Add(_widgetItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(Strings.Tray_Preferences, "Icon.Settings.Regular", OnPreferences));
        // "Restart to update to X.Y.Z" while a downloaded update waits.
        _updateSeparator = new Separator { Visibility = Visibility.Collapsed };
        _updateItem = Item("", "Icon.Download.Regular", OnRestartToUpdate);
        _updateItem.Visibility = Visibility.Collapsed;
        menu.Items.Add(_updateSeparator);
        menu.Items.Add(_updateItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(Strings.Tray_Exit, null, (_, _) => ((App)Application.Current).ExitApplication()));
        _menu = menu;

        _status = _services.GetService<IHotkeyStatusSource>();
        _coordinator = _services.GetService<ICaptureCoordinator>() as CaptureCoordinator;
        _normalIcon = LoadIcon(recording: false);
        _recordingIcon = LoadIcon(recording: true);
        if (_status is not null) _status.Changed += OnHotkeysChanged;
        if (_coordinator is not null) _coordinator.RecordingChanged += OnRecordingChanged;
        _updates = _services.GetService<IUpdateService>();
        if (_updates is not null)
        {
            _updates.StatusChanged += OnUpdateStatus;
            ApplyUpdateStatus(_updates.Status);
        }
    }

    /// <summary>(Re)creates the TaskbarIcon; throws when the shell refuses it (handled by <see cref="TrayIconRecovery"/>).</summary>
    private bool CreateIcon()
    {
        if (_icon is not null)
        {
            _icon.ContextMenu = null;
            _icon.Dispose();
            _icon = null;
        }
        var icon = IconFactory();
        try
        {
            icon.ContextMenu = _menu;
            icon.MenuActivation = PopupActivationMode.RightClick;
            icon.NoLeftClickDelay = true;
            icon.LeftClickCommand = new RelayCommand(() => OnOpenEditor(this, new RoutedEventArgs()));
            var current = _recording ? _recordingIcon ?? _normalIcon : _normalIcon;
            if (current is not null) icon.Icon = current;
            _icon = icon;
            UpdateHotkeyTexts();
            icon.ForceCreate(enablesEfficiencyMode: false);
            return icon.IsCreated;
        }
        catch
        {
            _icon = null;
            icon.ContextMenu = null;
            try { icon.Dispose(); } catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
            throw;
        }
    }

    /// <summary>
    /// Hidden top-level window that receives Explorer's broadcast <c>TaskbarCreated</c> (message-only windows do not get
    /// broadcasts), so a missing icon is created as soon as the notification area exists.
    /// </summary>
    private void StartShellListener()
    {
        if (_shellListener is not null || TrayIconRecovery.TaskbarCreatedMessage == 0) return;
        try
        {
            var p = new System.Windows.Interop.HwndSourceParameters("Shotwise.ShellListener") { Width = 0, Height = 0, WindowStyle = unchecked((int)0x80000000) /* WS_POPUP, never shown */ };
            _shellListener = new System.Windows.Interop.HwndSource(p);
            _shellListener.AddHook(ShellListenerHook);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _log.LogWarning(ex, "Taskbar restart listener unavailable; a missing tray icon is not retried");
        }
    }

    private IntPtr ShellListenerHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_recovery is not null && _recovery.OnWindowMessage(unchecked((uint)msg))) handled = false;   // others may also listen
        return IntPtr.Zero;
    }

    /// <summary>Delivers a window message to the recovery logic (tests simulate <c>TaskbarCreated</c> with this).</summary>
    internal bool DeliverShellMessage(uint msg) => _recovery?.OnWindowMessage(msg) ?? false;

    private void OnUpdateStatus(object? sender, UpdateStatus s) => Application.Current?.Dispatcher.BeginInvoke(new Action(() => ApplyUpdateStatus(s)));

    private void ApplyUpdateStatus(UpdateStatus s)
    {
        if (_updateItem is null || _updateSeparator is null) return;
        var ready = s.State == UpdateState.ReadyToRestart && s.Update is not null;
        if (ready) _updateItem.Header = Shotwise.UI.Capture.CS.Format("Update_TrayRestart", s.Update!.Version);
        _updateItem.Visibility = _updateSeparator.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRestartToUpdate(object? sender, RoutedEventArgs e)
        => _services.GetService<Hosting.UpdateNotifier>()?.RestartNow();

    /// <summary>Loads the tray .ico at the small-icon size for the current DPI (16 px at 100 %, 24 at 150 %, 32 at 200 %).</summary>
    private System.Drawing.Icon? LoadIcon(bool recording)
    {
        try
        {
            using var stream = AppIcon.OpenTrayIcon(recording);
            if (stream is null) return null;
            int size = Math.Max(16, GetSystemMetrics(49 /* SM_CXSMICON */));
            return new System.Drawing.Icon(stream, size, size);
        }
        catch (Exception ex) when (ex is ArgumentException or System.IO.IOException or InvalidOperationException)
        {
            _log.LogWarning(ex, "Tray icon could not be loaded");
            return null;
        }
    }

    private void OnRecordingChanged(object? sender, EventArgs e) => Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
    {
        if (sender is not CaptureCoordinator c) return;
        _recording = c.IsRecording;
        if (_icon is null) return;
        var next = _recording ? _recordingIcon : _normalIcon;
        if (next is not null) _icon.Icon = next;
        UpdateHotkeyTexts();
    }));

    private void OnHotkeysChanged(object? sender, EventArgs e) => Application.Current?.Dispatcher.BeginInvoke(new Action(UpdateHotkeyTexts));

    /// <summary>Tooltip + menu shortcut texts from the effective registrations (falls back to the configured key).</summary>
    private void UpdateHotkeyTexts()
    {
        if (_icon is null) return;
        var captureKey = _status?.Find(HotkeyIds.GlobalCapture)?.Effective
            ?? _services.GetRequiredService<ISettingsService>().Current.Hotkeys.GlobalCapture;
        var text = captureKey is { IsEmpty: false } k ? k.ToDisplayString() : "";
        _icon.ToolTipText = _recording ? "Shotwise - " + Strings.Tray_Recording : string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Tray_ToolTip, text);
        if (_captureItem is not null) _captureItem.InputGestureText = text;
    }

    private static MenuItem Item(string header, string? iconKey, RoutedEventHandler click)
    {
        var item = new MenuItem
        {
            Header = header,
            Icon = iconKey is null ? new Border { Width = 16, Height = 16 } : new SwIcon { Source = iconKey, Size = 16 },
        };
        item.Click += click;
        return item;
    }

    private void OnCapture(object? sender, RoutedEventArgs e)
    {
        if (_services.GetService<ICaptureCoordinator>() is { } c) _ = c.RunAsync(_services.GetRequiredService<IPresetRepository>().Active);
        else ((App)Application.Current).ShowMainWindow();
    }

    private void OnOpenEditor(object? sender, RoutedEventArgs e)
    {
        if (_services.GetService<IEditorHost>() is { } host) host.ShowEditor();
        else ((App)Application.Current).ShowMainWindow();
    }

    private void OnOpenCaptureWindow(object? sender, RoutedEventArgs e)
    {
        if (_services.GetService<ICaptureCoordinator>() is { } c) c.ShowCaptureWindow();
        else ((App)Application.Current).ShowMainWindow();
    }

    private void OnToggleWidget(object? sender, RoutedEventArgs e)
    {
        var show = _widgetItem?.IsChecked == true;
        _services.GetRequiredService<ISettingsService>().Update(s => s.General.ShowCaptureWidget = show);
        _services.GetService<ICaptureCoordinator>()?.ToggleWidget(show);
    }

    private void OnPreferences(object? sender, RoutedEventArgs e)
    {
        if (_services.GetService<IDialogService>() is { } dialogs) _ = dialogs.ShowPreferences(new PreferencesRequest());
        else ((App)Application.Current).ShowMainWindow();
    }

    public void Dispose()
    {
        // Called by App.OnExit and again by the container: never resolve services here (the provider may already be disposed).
        if (_status is not null) { _status.Changed -= OnHotkeysChanged; _status = null; }
        if (_coordinator is not null) { _coordinator.RecordingChanged -= OnRecordingChanged; _coordinator = null; }
        if (_updates is not null) { _updates.StatusChanged -= OnUpdateStatus; _updates = null; }
        if (_shellListener is not null)
        {
            _shellListener.RemoveHook(ShellListenerHook);
            _shellListener.Dispose();
            _shellListener = null;
        }
        _icon?.Dispose();
        _icon = null;
        _normalIcon?.Dispose();
        _recordingIcon?.Dispose();
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    private sealed class RelayCommand : System.Windows.Input.ICommand
    {
        private readonly Action _execute;
        public RelayCommand(Action execute) => _execute = execute;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute();
    }
}
