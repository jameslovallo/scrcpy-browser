using System.Collections.ObjectModel;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using ScrcpyLauncher.Interop;
using ScrcpyLauncher.Models;
using ScrcpyLauncher.Services;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace ScrcpyLauncher;

public sealed partial class MainWindow : Window
{
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(30);

    private readonly AppSettings _settings;
    private readonly AndroidDeviceService _deviceService;
    private readonly IconService _iconService;
    private readonly MediaSessionService _mediaSessionService;
    private readonly IntPtr _hwnd;

    private readonly List<DeviceInfo> _devices = [];
    private readonly List<ScrcpySession> _sessions = [];
    private readonly ObservableCollection<ScrcpySession> _running = [];
    private readonly ObservableCollection<PinnedTile> _pinned = [];
    private readonly ObservableCollection<AndroidApp> _suggestions = [];
    private readonly ObservableCollection<AndroidApp> _hiddenApps = [];
    private readonly ObservableCollection<AndroidApp> _installedApps = [];
    private readonly MenuFlyout _itemMenu = new() { ShouldConstrainToRootBounds = false };
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _layoutTimer;
    private readonly KeyboardShortcuts _shortcuts;

    private IReadOnlyList<AndroidApp> _allApps = [];
    private DeviceInfo? _device;
    private ScrcpyAudioSession? _audioSession;
    private MediaSnapshot? _mediaSnapshot;
    private CancellationTokenSource? _appsCts;
    private CancellationTokenSource? _mediaPollCts;
    private ScrcpySession? _current;
    private bool _audioCaptureEnabled = true;
    private bool _syncingSelection;
    private bool _searchFocusFromUser;
    private bool _settingsOpen;

    public MainWindow()
    {
        InitializeComponent();

        _settings = AppSettings.Load();
        _deviceService = new AndroidDeviceService(_settings);
        _iconService = new IconService(_deviceService.AdbPath);
        _mediaSessionService = new MediaSessionService(_deviceService.AdbPath);
        _hwnd = WindowNative.GetWindowHandle(this);

        if (Enum.TryParse<ElementTheme>(_settings.Theme, ignoreCase: true, out var theme) && theme != ElementTheme.Default)
        {
            RootGrid.RequestedTheme = theme;
        }

        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(BundledAssets.AppIconPath);
        AppWindow.Resize(new SizeInt32(1400, 900));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 700;
            presenter.PreferredMinimumHeight = 500;
        }

        PinnedGrid.ItemsSource = _pinned;
        RunningList.ItemsSource = _running;
        SearchBox.ItemsSource = _suggestions;
        HiddenAppsList.ItemsSource = _hiddenApps;
        InstalledAppsList.ItemsSource = _installedApps;
        SearchBox.GettingFocus += (_, args) => _searchFocusFromUser = args.InputDevice is FocusInputDeviceKind.Mouse or FocusInputDeviceKind.Touch or FocusInputDeviceKind.Pen;
        _itemMenu.Opening += ItemMenu_Opening;

        HostArea.SizeChanged += (_, _) => LayoutCurrentSession();
        AppTitleBar.SizeChanged += (_, _) => AlignSidebarHeader();
        SidebarHeader.SizeChanged += (_, _) => AlignAppTitle();
        HostCard.SizeChanged += (_, _) => AlignAppTitle();
        RootGrid.ActualThemeChanged += (_, _) => UpdateCaptionButtonColors();
        RootGrid.Loaded += OnLoaded;
        Closed += OnClosed;

        // SDL repositions its window on its own (initial show, rotation, ...); keep embedded windows where they belong.
        _layoutTimer = DispatcherQueue.CreateTimer();
        _layoutTimer.Interval = TimeSpan.FromMilliseconds(250);
        _layoutTimer.Tick += (_, _) => EnforceLayout();
        _layoutTimer.Start();

        // Hook callbacks must return quickly, so the actions are queued.
        _shortcuts = new KeyboardShortcuts(_hwnd, new()
        {
            ['W'] = () => DispatcherQueue.TryEnqueue(CloseCurrentSession),
            ['T'] = () => DispatcherQueue.TryEnqueue(FocusSearch),
            ['F'] = () => DispatcherQueue.TryEnqueue(FocusSearch),
        });

        RefreshSidebarState();
        UpdateAudioCaptureButton();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RootGrid.XamlRoot.Changed += (_, _) =>
        {
            LayoutCurrentSession();
            AlignAppTitle();
        };
        UpdateCaptionButtonColors();
        AlignSidebarHeader();
        AlignAppTitle();

        if (_deviceService.ScrcpyPath is null)
        {
            ShowStatus("scrcpy not found", "Install scrcpy and make sure it is on PATH, or set ScrcpyPath in settings.json.", InfoBarSeverity.Error);
            return;
        }
        await RefreshDevicesAsync();
    }

    #region Title bar

    /// <summary>Matches the system caption buttons (min/max/close) to the current light/dark theme.</summary>
    private void UpdateCaptionButtonColors()
    {
        var dark = RootGrid.ActualTheme == ElementTheme.Dark;
        var tb = AppWindow.TitleBar;
        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        tb.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
        tb.ButtonPressedForegroundColor = dark ? Colors.White : Colors.Black;
        tb.ButtonInactiveForegroundColor = dark ? Color.FromArgb(0x5D, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x5C, 0x00, 0x00, 0x00);
        tb.ButtonHoverBackgroundColor = dark ? Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x09, 0x00, 0x00, 0x00);
        tb.ButtonPressedBackgroundColor = dark ? Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x06, 0x00, 0x00, 0x00);
    }

    /// <summary>
    /// Lines the device name up with the sidebar content (search box) left edge, and
    /// stretches the header to the sidebar's right edge so the focused-app title starts over the view.
    /// </summary>
    private void AlignSidebarHeader()
    {
        if (SidebarHeader.XamlRoot is null) return;
        var naturalLeft = SidebarHeader.TransformToVisual(RootGrid).TransformPoint(new Point(0, 0)).X - SidebarHeader.Margin.Left;
        var contentLeft = Sidebar.Padding.Left - DeviceButton.Padding.Left - DeviceButton.BorderThickness.Left;
        var contentRight = SidebarColumn.ActualWidth - Sidebar.Padding.Right;
        var indent = contentLeft - naturalLeft;
        var width = Math.Max(0, contentRight - naturalLeft - indent);
        if (Math.Abs(SidebarHeader.Margin.Left - indent) > 0.5) SidebarHeader.Margin = new Thickness(indent, 0, 0, 0);
        if (Math.Abs(SidebarHeader.Width - width) > 0.5 || double.IsNaN(SidebarHeader.Width)) SidebarHeader.Width = width;
    }

    /// <summary>Aligns the focused-app title with the left edge of the scrcpy view (the embedded window fills HostArea).</summary>
    private void AlignAppTitle()
    {
        if (AppTitleText.XamlRoot is null) return;
        var viewLeft = HostArea.TransformToVisual(RootGrid).TransformPoint(new Point(0, 0)).X;
        var naturalLeft = AppTitleText.TransformToVisual(RootGrid).TransformPoint(new Point(0, 0)).X - AppTitleText.Margin.Left;
        var indent = Math.Max(0, viewLeft - naturalLeft);
        if (Math.Abs(AppTitleText.Margin.Left - indent) > 0.5) AppTitleText.Margin = new Thickness(indent, 0, 0, 0);
    }

    private void UpdateAppTitle() =>
        AppTitleText.Text = _settingsOpen ? "Settings" : _current?.Name ?? string.Empty;

    private void DeviceMenu_Opening(object? sender, object e)
    {
        DeviceMenu.Items.Clear();
        foreach (var device in _devices)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = device.Display,
                GroupName = "devices",
                IsChecked = device == _device,
                Icon = new FontIcon { Glyph = "\uE8EA" },
            };
            item.Click += async (_, _) => await SelectDeviceAsync(device);
            DeviceMenu.Items.Add(item);
        }
        if (_devices.Count == 0) DeviceMenu.Items.Add(new MenuFlyoutItem { Text = "No devices connected", IsEnabled = false });

        DeviceMenu.Items.Add(new MenuFlyoutSeparator());
        var refresh = new MenuFlyoutItem { Text = "Refresh devices", Icon = new FontIcon { Glyph = "\uE72C" } };
        refresh.Click += async (_, _) => await RefreshDevicesAsync();
        DeviceMenu.Items.Add(refresh);
        var reload = new MenuFlyoutItem { Text = "Reload apps", Icon = new FontIcon { Glyph = "\uE71D" }, IsEnabled = _device is { IsReady: true } };
        reload.Click += async (_, _) =>
        {
            _iconService.ResetFailures();
            await LoadAppsAsync();
        };
        DeviceMenu.Items.Add(reload);
    }

    private async void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { IsEmbedded: true } session) return;
        WindowEmbedder.Focus(session.WindowHandle);
        if (session.DisplayId is not { } displayId || _deviceService.AdbPath is not { } adb) return;
        try
        {
            await ProcessRunner.RunAsync(adb, ["-s", session.Serial, "shell", "input", "-d", displayId.ToString(), "keyevent", "KEYCODE_BACK"]);
        }
        catch (Exception ex)
        {
            ShowStatus("Back failed", ex.Message, InfoBarSeverity.Warning);
        }
    }

    private void FocusSearch()
    {
        // Take Win32 focus back from an embedded scrcpy window before focusing the XAML control.
        NativeMethods.SetFocus(_hwnd);
        SearchBox.Focus(FocusState.Keyboard);
        OpenSuggestions();
    }

    private async void CloseCurrentSession()
    {
        if (_settingsOpen)
        {
            SetSettingsOpen(false);
            return;
        }
        if (_current is not { } session) return;
        session.Status = "Closing…";
        await session.CloseAsync(TimeSpan.FromSeconds(2));
    }

    #endregion

    #region Devices & apps

    private async Task RefreshDevicesAsync()
    {
        var previous = _device?.Serial;
        try
        {
            var devices = await _deviceService.GetDevicesAsync();
            _devices.Clear();
            _devices.AddRange(devices);

            var next =
                _devices.FirstOrDefault(d => d.Serial == previous) ??
                _devices.FirstOrDefault(d => d.IsReady) ??
                _devices.FirstOrDefault();
            await SelectDeviceAsync(next);

            if (_devices.Count == 0)
            {
                ShowStatus("No device", "Connect an Android device with USB debugging enabled, then refresh from the device menu.", InfoBarSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            ShowStatus("adb error", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task SelectDeviceAsync(DeviceInfo? device)
    {
        var changed = device?.Serial != _device?.Serial || device?.State != _device?.State;
        _device = device;
        ToolTipService.SetToolTip(DeviceButton, device?.Display ?? "No device");
        DeviceNameText.Text = device?.Name ?? "No device";
        DeviceStatusDot.Visibility = device is { IsReady: true } ? Visibility.Collapsed : Visibility.Visible;
        StartMediaPolling();
        await SyncAudioCaptureSessionAsync();
        if (changed || _allApps.Count == 0) await LoadAppsAsync();
    }

    private void StartMediaPolling()
    {
        _mediaPollCts?.Cancel();
        _mediaSnapshot = null;
        UpdateMediaCard(null);
        if (_device is not { IsReady: true } device || _deviceService.AdbPath is null) return;

        var cts = _mediaPollCts = new CancellationTokenSource();
        _ = PollMediaAsync(device.Serial, cts.Token);
    }

    private async Task PollMediaAsync(string serial, CancellationToken cancellationToken)
    {
        var failureReported = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = await _mediaSessionService.GetSnapshotAsync(serial, cancellationToken);
                failureReported = false;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_device?.Serial != serial || HasSameMediaCardContent(_mediaSnapshot, snapshot)) return;
                    _mediaSnapshot = snapshot;
                    UpdateMediaCard(snapshot);
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!failureReported)
                {
                    failureReported = true;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_device?.Serial == serial)
                            ShowStatus("Media information unavailable", ex.Message, InfoBarSeverity.Warning);
                    });
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private static bool HasSameMediaCardContent(MediaSnapshot? current, MediaSnapshot? next) =>
        current is null
            ? next is null
            : next is not null &&
              current.Serial == next.Serial &&
              current.PackageName == next.PackageName &&
              current.Title == next.Title &&
              current.Artist == next.Artist &&
              current.Album == next.Album &&
              current.PlaybackState == next.PlaybackState &&
              current.Actions == next.Actions &&
              current.HasArtwork == next.HasArtwork &&
              current.ArtworkPath == next.ArtworkPath;

    private void UpdateMediaCard(MediaSnapshot? snapshot)
    {
        MediaCard.Visibility = snapshot is null ? Visibility.Collapsed : Visibility.Visible;
        if (snapshot is null)
        {
            MediaArtwork.Source = null;
            MediaArtwork.Visibility = Visibility.Collapsed;
            MediaArtworkPlaceholder.Visibility = Visibility.Visible;
            MediaTitleText.Text = string.Empty;
            MediaArtistText.Text = string.Empty;
            return;
        }

        MediaTitleText.Text = snapshot.Title;
        MediaArtistText.Text = string.IsNullOrWhiteSpace(snapshot.Artist)
            ? snapshot.Album
            : snapshot.Artist;
        MediaArtwork.Source = snapshot.ArtworkPath is null ? null : new BitmapImage(new Uri(snapshot.ArtworkPath));
        MediaArtwork.Visibility = snapshot.ArtworkPath is null ? Visibility.Collapsed : Visibility.Visible;
        MediaArtworkPlaceholder.Visibility = snapshot.ArtworkPath is null ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(MediaArtworkButton, $"Open {snapshot.Title}");
        ToolTipService.SetToolTip(MediaArtworkButton, $"Open {snapshot.Title} in scrcpy");
        MediaPreviousButton.IsEnabled = snapshot.CanPrevious;
        MediaNextButton.IsEnabled = snapshot.CanNext;
        MediaPlaybackButton.IsEnabled = snapshot.CanTogglePlayback;
        MediaPlaybackIcon.Glyph = snapshot.IsPlaying ? "\uE769" : "\uE768";
        AutomationProperties.SetName(MediaPlaybackButton, snapshot.IsPlaying ? "Pause" : "Play");
    }

    private async void MediaArtworkButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaSnapshot is not { } snapshot || _device?.Serial != snapshot.Serial) return;
        try
        {
            var app = _allApps.FirstOrDefault(a => string.Equals(a.Package, snapshot.PackageName, StringComparison.OrdinalIgnoreCase))
                ?? await _deviceService.GetAppByPackageAsync(snapshot.Serial, snapshot.PackageName)
                ?? new AndroidApp(snapshot.Title, snapshot.PackageName, isSystem: true);
            await LaunchOrSwitchAsync(app, snapshot.Serial);
        }
        catch (Exception ex)
        {
            ShowStatus("Could not open media app", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void MediaPreviousButton_Click(object sender, RoutedEventArgs e) =>
        await DispatchMediaCommandAsync("previous");

    private async void MediaNextButton_Click(object sender, RoutedEventArgs e) =>
        await DispatchMediaCommandAsync("next");

    private async void MediaPlaybackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaSnapshot is not { } snapshot) return;
        var command = (snapshot.Actions & 512) != 0
            ? "play-pause"
            : snapshot.IsPlaying ? "pause" : "play";
        await DispatchMediaCommandAsync(command);
    }

    private async Task DispatchMediaCommandAsync(string command)
    {
        if (_mediaSnapshot is not { } snapshot) return;
        try
        {
            await _mediaSessionService.DispatchAsync(snapshot.Serial, command);
        }
        catch (Exception ex)
        {
            ShowStatus("Media control failed", ex.Message, InfoBarSeverity.Warning);
        }
    }

    private async void AudioCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (_device is not { IsReady: true })
        {
            ShowStatus("No device", "Select a connected device to capture audio.", InfoBarSeverity.Warning);
            return;
        }

        _audioCaptureEnabled = _audioSession?.IsRunning != true;
        await SyncAudioCaptureSessionAsync();
    }

    private async Task SyncAudioCaptureSessionAsync()
    {
        var device = _device is { IsReady: true } readyDevice ? readyDevice : null;
        if (_audioSession is { } current &&
            (!_audioCaptureEnabled || device is null || current.Serial != device.Serial || !current.IsRunning))
        {
            if (!await StopAudioCaptureSessionAsync()) return;
        }

        if (!_audioCaptureEnabled || device is null)
        {
            UpdateAudioCaptureButton();
            return;
        }

        if (_audioSession is { IsRunning: true } active && active.Serial == device.Serial)
        {
            UpdateAudioCaptureButton();
            return;
        }

        if (_deviceService.ScrcpyPath is not { } scrcpyPath) return;

        var session = new ScrcpyAudioSession(device.Serial);
        _audioSession = session;
        session.Exited += AudioCapture_Exited;
        try
        {
            session.Start(scrcpyPath, _settings.ExtraScrcpyArgs);
            UpdateAudioCaptureButton();
        }
        catch (Exception ex)
        {
            session.Exited -= AudioCapture_Exited;
            _audioSession = null;
            _audioCaptureEnabled = false;
            await session.CloseAsync();
            UpdateAudioCaptureButton();
            ShowStatus("Failed to start audio capture", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task<bool> StopAudioCaptureSessionAsync()
    {
        var session = _audioSession;
        if (session is null)
        {
            UpdateAudioCaptureButton();
            return true;
        }

        session.Exited -= AudioCapture_Exited;
        try
        {
            await session.CloseAsync();
            if (ReferenceEquals(_audioSession, session)) _audioSession = null;
            UpdateAudioCaptureButton();
            return true;
        }
        catch (Exception ex)
        {
            ShowStatus("Failed to stop audio capture", ex.Message, InfoBarSeverity.Error);
            UpdateAudioCaptureButton();
            return false;
        }
    }

    private void UpdateAudioCaptureButton()
    {
        var isCapturing = _audioSession?.IsRunning == true;
        var hasDevice = _device is { IsReady: true } && _deviceService.ScrcpyPath is not null;
        AudioCaptureIcon.Glyph = isCapturing ? "\uE767" : "\uE74F";
        AudioCaptureButton.IsEnabled = hasDevice;
        var action = isCapturing ? "Disable" : "Enable";
        var description = hasDevice ? $"{action} device audio capture" : "Connect a device to capture audio";
        ToolTipService.SetToolTip(AudioCaptureButton, description);
        AutomationProperties.SetName(AudioCaptureButton, description);
    }

    private void AudioCapture_Exited(object? sender, EventArgs e)
    {
        if (sender is not ScrcpyAudioSession session) return;
        DispatcherQueue.TryEnqueue(() => _ = HandleAudioCaptureExitedAsync(session));
    }

    private async Task HandleAudioCaptureExitedAsync(ScrcpyAudioSession session)
    {
        if (!ReferenceEquals(_audioSession, session)) return;

        _audioSession = null;
        _audioCaptureEnabled = false;
        UpdateAudioCaptureButton();
        var exitCode = session.ExitCode;
        var log = session.RecentLog;
        try
        {
            await session.CloseAsync();
        }
        catch (Exception ex)
        {
            ShowStatus("Failed to clean up audio capture", ex.Message, InfoBarSeverity.Error);
        }

        var details = string.IsNullOrEmpty(log)
            ? $"scrcpy exited unexpectedly (code {exitCode?.ToString() ?? "unknown"})."
            : LastLines(log, 4);
        ShowStatus("Audio capture stopped", details, InfoBarSeverity.Warning);
    }

    private async Task LoadAppsAsync()
    {
        _appsCts?.Cancel();
        _allApps = [];
        RebuildPinned();
        ApplyFilter();

        if (_device is not { } device) return;
        if (!device.IsReady)
        {
            ShowStatus("Device not ready", $"Device state is '{device.State}'. Authorize USB debugging on the device and refresh.", InfoBarSeverity.Warning);
            return;
        }

        var cts = _appsCts = new CancellationTokenSource();
        try
        {
            var apps = await _deviceService.GetAppsAsync(device.Serial, cts.Token);
            if (cts.IsCancellationRequested) return;
            _allApps = apps;
            RebuildPinned();
            ApplyFilter();
            StatusBar.IsOpen = false;
            _ = LoadIconsAsync(device.Serial, apps, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowStatus("Could not list apps", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task LoadIconsAsync(string serial, IReadOnlyList<AndroidApp> apps, CancellationToken cancellationToken)
    {
        var byPackage = apps.GroupBy(a => a.Package).ToDictionary(g => g.Key, g => g.First());
        try
        {
            await Task.Run(() => _iconService.LoadIconsAsync(serial, byPackage.Keys.ToList(), (pkg, path) =>
            {
                if (byPackage.TryGetValue(pkg, out var app)) DispatcherQueue.TryEnqueue(() => app.SetIconFile(path));
            }, cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Icons are cosmetic; the letter tiles remain.
        }
    }

    private bool IsHidden(AndroidApp app) => _settings.HiddenApps.Contains(app.Package);

    private void ApplyFilter()
    {
        var query = SearchBox.Text?.Trim() ?? string.Empty;
        var visible = _allApps.Where(a => !IsHidden(a));
        var matches = query.Length == 0
            ? visible
            : visible.Where(a => a.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                                 a.Package.Contains(query, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(a => a.Name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1);

        _suggestions.Clear();
        foreach (var app in matches) _suggestions.Add(app);
        RefreshSettingsLists();
    }

    private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        SearchBox.Translation = new System.Numerics.Vector3(0, 0, SearchFocusElevation);
        // Only a click opens the list: focus also returns here when the list closes (Esc) or the window is reactivated.
        if (!_searchFocusFromUser) return;
        _searchFocusFromUser = false;
        OpenSuggestions();
    }

    // Focus is shown by raising the box (ThemeShadow depth) rather than an accent underline.
    private const float SearchFocusElevation = 16;

    private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        for (var el = FocusManager.GetFocusedElement(SearchBox.XamlRoot) as DependencyObject; el is not null; el = VisualTreeHelper.GetParent(el))
            if (el == SearchBox) return;
        SearchBox.Translation = System.Numerics.Vector3.Zero;
    }

    private void OpenSuggestions()
    {
        ApplyFilter();
        SearchBox.IsSuggestionListOpen = _suggestions.Count > 0;
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        ApplyFilter();
        sender.IsSuggestionListOpen = _suggestions.Count > 0;
    }

    private async void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var query = args.QueryText?.Trim() ?? string.Empty;
        var app = args.ChosenSuggestion as AndroidApp ?? _suggestions.FirstOrDefault();
        if (app is null && query.Length == 0) return;
        sender.Text = string.Empty;
        sender.IsSuggestionListOpen = false;
        if (app is not null)
        {
            await LaunchOrSwitchAsync(app);
            return;
        }
        await SearchWebAsync(query);
    }

    private async Task SearchWebAsync(string query)
    {
        if (_device is not { IsReady: true } device)
        {
            ShowStatus("No device", "Select a connected device to search the web.", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            var browserPackage = await _deviceService.GetDefaultBrowserPackageAsync(device.Serial);
            var browserApp = _allApps.FirstOrDefault(a => string.Equals(a.Package, browserPackage, StringComparison.OrdinalIgnoreCase))
                ?? await _deviceService.GetAppByPackageAsync(device.Serial, browserPackage)
                ?? new AndroidApp(browserPackage, browserPackage, true);
            var session = await LaunchOrSwitchAsync(browserApp, device.Serial);
            if (session is null) return;

            var displayId = await session.WaitForDisplayIdAsync(TimeSpan.FromSeconds(5));
            if (displayId is null)
            {
                throw new InvalidOperationException("scrcpy did not report the browser session's virtual display ID.");
            }

            await _deviceService.OpenBrowserSearchAsync(device.Serial, browserPackage, displayId.Value, query);
        }
        catch (Exception ex)
        {
            ShowStatus("Web search failed", ex.Message, InfoBarSeverity.Error);
        }
    }

    #endregion

    #region Sidebar (pinned + running)

    private void RebuildPinned()
    {
        _pinned.Clear();
        foreach (var package in _settings.PinnedApps)
        {
            if (_allApps.FirstOrDefault(a => a.Package == package) is { } app && !IsHidden(app)) _pinned.Add(new PinnedTile(app));
        }
        RefreshSidebarState();
    }

    private bool IsShownAsPinned(ScrcpySession session) =>
        session.Serial == _device?.Serial && _pinned.Any(t => t.App.Package == session.App.Package);

    private ScrcpySession? FindSession(string? serial, string package) =>
        _sessions.FirstOrDefault(s => s.Serial == serial && s.App.Package == package);

    /// <summary>Syncs the pinned tile highlight, the running list (unpinned sessions), selection and title with <see cref="_current"/>.</summary>
    private void RefreshSidebarState()
    {
        foreach (var tile in _pinned)
        {
            var session = FindSession(_device?.Serial, tile.App.Package);
            tile.IsRunning = session is not null;
            tile.IsActive = _current is not null && _current == session;
        }

        _syncingSelection = true;
        try
        {
            var unpinned = _sessions.Where(s => !IsShownAsPinned(s)).ToList();
            if (!unpinned.SequenceEqual(_running))
            {
                _running.Clear();
                foreach (var s in unpinned) _running.Add(s);
            }
            RunningList.SelectedItem = _current is not null && _running.Contains(_current) ? _current : null;
        }
        finally
        {
            _syncingSelection = false;
        }

        PinnedGrid.Visibility = _pinned.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RunningDivider.Visibility = _pinned.Count > 0 && _running.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BackButton.IsEnabled = _current is { IsEmbedded: true } && !_settingsOpen;
        UpdateAppTitle();
    }

    private async void PinnedGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PinnedTile tile) await LaunchOrSwitchAsync(tile.App);
    }

    private void PinnedGrid_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        var reorderedPackages = _pinned.Select(tile => tile.App.Package).ToList();
        var visiblePackages = reorderedPackages.ToHashSet(StringComparer.Ordinal);
        var visibleSlots = _settings.PinnedApps
            .Select((package, index) => (package, index))
            .Where(item => visiblePackages.Contains(item.package))
            .Select(item => item.index)
            .ToList();

        for (var i = 0; i < Math.Min(visibleSlots.Count, reorderedPackages.Count); i++)
            _settings.PinnedApps[visibleSlots[i]] = reorderedPackages[i];

        _settings.Save();
    }

    private void RunningList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || RunningList.SelectedItem is not ScrcpySession session) return;
        SetSettingsOpen(false);
        ActivateSession(session);
    }

    private void PinnedGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args) =>
        args.ItemContainer.ContextFlyout ??= _itemMenu;

    private void RunningList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args) =>
        args.ItemContainer.ContextFlyout ??= _itemMenu;

    /// <summary>Context menu for pinned tiles and running apps: pin/unpin, relaunch, close.</summary>
    private void ItemMenu_Opening(object? sender, object e)
    {
        _itemMenu.Items.Clear();
        var (app, session) = (_itemMenu.Target as SelectorItem)?.Content switch
        {
            PinnedTile t => (t.App, FindSession(_device?.Serial, t.App.Package)),
            ScrcpySession s => (s.App, s),
            _ => ((AndroidApp?)null, (ScrcpySession?)null),
        };
        if (app is null) return;

        var pinned = _settings.PinnedApps.Contains(app.Package);
        var pin = new MenuFlyoutItem { Text = pinned ? "Unpin" : "Pin", Icon = new FontIcon { Glyph = pinned ? "\uE77A" : "\uE718" } };
        pin.Click += (_, _) => SetPinned(app, !pinned);
        _itemMenu.Items.Add(pin);

        var relaunch = new MenuFlyoutItem { Text = session is null ? "Launch" : "Relaunch", Icon = new FontIcon { Glyph = "\uE72C" } };
        relaunch.Click += async (_, _) => await RelaunchAsync(app, session);
        _itemMenu.Items.Add(relaunch);

        var close = new MenuFlyoutItem { Text = "Close", Icon = new FontIcon { Glyph = "\uE711" }, IsEnabled = session is not null };
        close.Click += async (_, _) => { if (session is not null) await session.CloseAsync(TimeSpan.FromSeconds(2)); };
        _itemMenu.Items.Add(close);

        var hide = new MenuFlyoutItem { Text = "Hide", Icon = new FontIcon { Glyph = "\uED1A" } };
        hide.Click += (_, _) => SetHidden(app, true);
        _itemMenu.Items.Add(hide);

        _itemMenu.Items.Add(new MenuFlyoutSeparator());
        var capture = new ToggleMenuFlyoutItem
        {
            Text = "Capture mouse",
            Icon = new FontIcon { Glyph = "\uE962" },
            IsChecked = _settings.CaptureMouseApps.Contains(app.Package),
        };
        ToolTipService.SetToolTip(capture, "Applies on next launch. Press Alt to release the mouse.");
        capture.Click += (_, _) => SetCaptureMouse(app, capture.IsChecked);
        _itemMenu.Items.Add(capture);
    }

    private void SetCaptureMouse(AndroidApp app, bool capture)
    {
        _settings.CaptureMouseApps.Remove(app.Package);
        if (capture) _settings.CaptureMouseApps.Add(app.Package);
        _settings.Save();
    }

    private void SetPinned(AndroidApp app, bool pinned)
    {
        _settings.PinnedApps.Remove(app.Package);
        if (pinned) _settings.PinnedApps.Add(app.Package);
        _settings.Save();
        RebuildPinned();
    }

    private void SetHidden(AndroidApp app, bool hidden)
    {
        _settings.HiddenApps.Remove(app.Package);
        if (hidden) _settings.HiddenApps.Add(app.Package);
        app.IsHidden = hidden;
        _settings.Save();
        RebuildPinned();
        ApplyFilter();
    }

    #endregion

    #region Settings view

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => SetSettingsOpen(!_settingsOpen);

    /// <summary>The settings view replaces the mirror; the embedded scrcpy window is hidden while it is open.</summary>
    private void SetSettingsOpen(bool open)
    {
        if (_settingsOpen == open) return;
        _settingsOpen = open;
        SettingsView.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open)
        {
            ManageSearchBox.Text = string.Empty;
            RefreshSettingsLists();
            // Keyboard input goes to the settings view, not the (hidden) scrcpy window.
            NativeMethods.SetFocus(_hwnd);
            SettingsView.ChangeView(null, 0, null, true);
        }
        ShowSession(_current);
        RefreshSidebarState();
    }

    private void RefreshSettingsLists()
    {
        foreach (var app in _allApps)
            app.IsHidden = IsHidden(app);

        Sync(_hiddenApps, _allApps.Where(IsHidden).ToList());
        NoHiddenAppsText.Visibility = _hiddenApps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var query = ManageSearchBox.Text?.Trim() ?? string.Empty;
        Sync(_installedApps, _allApps.Where(a => query.Length == 0 || a.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList());

        static void Sync(ObservableCollection<AndroidApp> target, List<AndroidApp> items)
        {
            if (items.SequenceEqual(target)) return;
            target.Clear();
            foreach (var item in items) target.Add(item);
        }
    }

    private void ManageSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => RefreshSettingsLists();

    private void RestoreApp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AndroidApp app) SetHidden(app, false);
    }

    private void ToggleHiddenApp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AndroidApp app)
            SetHidden(app, !app.IsHidden);
    }

    private async void UninstallApp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AndroidApp app || _device is not { IsReady: true } device) return;

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = $"Uninstall {app.Name}?",
            Content = app.IsSystem
                ? $"{app.Name} is a system app, so it will be removed for the current user on {device.Name}. Its data is deleted. It can be restored with \"adb shell cmd package install-existing {app.Package}\"."
                : $"{app.Name} and its data will be removed from {device.Name}.",
            PrimaryButtonText = "Uninstall",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        if (sender is Control button) button.IsEnabled = false;
        try
        {
            if (FindSession(device.Serial, app.Package) is { } session) await session.CloseAsync(TimeSpan.FromSeconds(2));
            await _deviceService.UninstallAsync(device.Serial, app);

            _allApps = _allApps.Where(a => a != app).ToList();
            _settings.HiddenApps.Remove(app.Package);
            _settings.PinnedApps.Remove(app.Package);
            _settings.CaptureMouseApps.Remove(app.Package);
            _settings.Save();
            RebuildPinned();
            ApplyFilter();
            ShowStatus("Uninstalled", $"{app.Name} was removed from {device.Name}.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus($"Could not uninstall {app.Name}", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            if (sender is Control b) b.IsEnabled = true;
        }
    }

    #endregion

    #region Sessions

    private async Task RelaunchAsync(AndroidApp app, ScrcpySession? existing)
    {
        var serial = existing?.Serial;
        if (existing is not null)
        {
            // Take it out of the list first so the relaunch doesn't just switch back to it.
            existing.Exited -= Session_Exited;
            _sessions.Remove(existing);
            if (_current == existing) _current = null;
            RefreshSidebarState();
            await existing.CloseAsync(TimeSpan.FromSeconds(2));
        }
        await LaunchOrSwitchAsync(app, serial);
    }

    private async Task<ScrcpySession?> LaunchOrSwitchAsync(AndroidApp app, string? serial = null)
    {
        SetSettingsOpen(false);
        serial ??= _device is { IsReady: true } d ? d.Serial : null;
        if (serial is null)
        {
            ShowStatus("No device", "Select a connected device first.", InfoBarSeverity.Warning);
            return null;
        }

        if (FindSession(serial, app.Package) is { } existing)
        {
            ActivateSession(existing);
            return existing;
        }

        var session = new ScrcpySession(app, serial, _settings.CaptureMouseApps.Contains(app.Package), GetDisplayDpi());
        session.Exited += Session_Exited;
        _sessions.Add(session);
        ActivateSession(session);

        try
        {
            session.Start(_deviceService.ScrcpyPath!, _settings.ExtraScrcpyArgs);
        }
        catch (Exception ex)
        {
            session.Exited -= Session_Exited;
            _sessions.Remove(session);
            ActivateSession(null);
            ShowStatus("Failed to start scrcpy", ex.Message, InfoBarSeverity.Error);
            return null;
        }

        var hwnd = await session.WaitForWindowAsync(WindowTimeout);
        if (hwnd == IntPtr.Zero)
        {
            // Exit (if any) is reported by Session_Exited; a timeout means scrcpy hung.
            if (!session.HasExited) await session.CloseAsync(TimeSpan.Zero);
            return null;
        }

        // Position the owned window over the main view; the flex display (-x) follows its size.
        WindowEmbedder.Embed(hwnd, _hwnd);
        session.MarkEmbedded(hwnd);

        if (_current == session) ActivateSession(session);
        else WindowEmbedder.Hide(hwnd);
        return session;
    }

    private void ActivateSession(ScrcpySession? session)
    {
        ShowSession(session);
        RefreshSidebarState();
    }

    private void Session_Exited(object? sender, EventArgs e)
    {
        if (sender is not ScrcpySession session) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            session.Exited -= Session_Exited;
            var index = _sessions.IndexOf(session);
            if (index < 0) return;

            if (!session.IsEmbedded)
            {
                var log = session.RecentLog;
                ShowStatus($"{session.Name} failed to start", string.IsNullOrEmpty(log) ? "scrcpy exited unexpectedly." : LastLines(log, 4), InfoBarSeverity.Error);
            }

            _sessions.RemoveAt(index);
            if (session.IsEmbedded) WindowEmbedder.Forget(session.WindowHandle);
            if (_current == session)
            {
                ActivateSession(_sessions.Count == 0 ? null : _sessions[Math.Min(index, _sessions.Count - 1)]);
            }
            else
            {
                RefreshSidebarState();
            }
        });
    }

    private async void CloseSession_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ScrcpySession session)
        {
            session.Status = "Closing…";
            await session.CloseAsync(TimeSpan.FromSeconds(2));
        }
    }

    private void ShowSession(ScrcpySession? session)
    {
        _current = session;

        foreach (var other in _sessions)
        {
            if (other != session && other.IsEmbedded) WindowEmbedder.Hide(other.WindowHandle);
        }

        if (session is { IsEmbedded: true })
        {
            Placeholder.Visibility = Visibility.Collapsed;
            LaunchProgress.IsActive = false;
            LayoutCurrentSession();
            if (!_settingsOpen && !WindowEmbedder.Focus(session.WindowHandle))
            {
                ShowStatus("Could not focus scrcpy window", "Mouse capture may not work until the scrcpy window receives input focus.", InfoBarSeverity.Warning);
            }
            return;
        }

        Placeholder.Visibility = _settingsOpen ? Visibility.Collapsed : Visibility.Visible;
        LaunchProgress.IsActive = session is not null && !_settingsOpen;
        PlaceholderIcon.Visibility = session is null ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderTitle.Text = session is null ? "No app selected" : $"Launching {session.Name}…";
        PlaceholderText.Text = session is null
            ? "Pick a pinned app, or search and press Enter, to start mirroring."
            : "Starting a virtual display on the device.";
    }

    private void LayoutCurrentSession()
    {
        if (_current is not { IsEmbedded: true } session) return;
        if (_settingsOpen)
        {
            WindowEmbedder.Hide(session.WindowHandle);
            return;
        }
        var (x, y, width, height) = GetHostRect();
        WindowEmbedder.Place(session.WindowHandle, _hwnd, x, y, width, height, GetHostCornerRadius());
    }

    /// <summary>Inner corner radius of the host card (outer radius minus border), in physical pixels.</summary>
    private int GetHostCornerRadius()
    {
        if (HostCard.XamlRoot is null) return 0;
        var inner = HostCard.CornerRadius.TopLeft - Math.Max(HostCard.BorderThickness.Left, HostCard.BorderThickness.Top);
        return (int)Math.Round(Math.Max(0, inner) * HostCard.XamlRoot.RasterizationScale);
    }

    private void EnforceLayout()
    {
        foreach (var s in _sessions)
        {
            if (s != _current && s.IsEmbedded) WindowEmbedder.Hide(s.WindowHandle);
        }
        LayoutCurrentSession();
    }

    /// <summary>
    /// Calibrated Android density for the launcher's content size across common host display settings.
    /// </summary>
    private int GetDisplayDpi()
    {
        return 180;
    }

    /// <summary>Host area bounds in physical pixels, relative to the window client area.</summary>
    private (int X, int Y, int Width, int Height) GetHostRect()
    {
        if (HostArea.XamlRoot is null) return (0, 0, 0, 0);
        var scale = HostArea.XamlRoot.RasterizationScale;
        var origin = HostArea.TransformToVisual(null).TransformPoint(new Point(0, 0));
        return (
            (int)Math.Round(origin.X * scale),
            (int)Math.Round(origin.Y * scale),
            (int)Math.Round(HostArea.ActualWidth * scale),
            (int)Math.Round(HostArea.ActualHeight * scale));
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _layoutTimer.Stop();
        _shortcuts.Dispose();
        _appsCts?.Cancel();
        _mediaPollCts?.Cancel();
        var sessions = _sessions.ToList();
        foreach (var s in sessions) s.Exited -= Session_Exited;
        var closeTasks = sessions
            .Select(s => Task.Run(() => s.CloseAsync(TimeSpan.FromSeconds(1.5))))
            .ToList();
        if (_audioSession is { } audioSession)
        {
            audioSession.Exited -= AudioCapture_Exited;
            _audioSession = null;
            closeTasks.Add(Task.Run(audioSession.CloseAsync));
        }
        // Close scrcpy before our window (their parent) is destroyed.
        Task.WaitAll(closeTasks.ToArray(), TimeSpan.FromSeconds(3));
        _iconService.Dispose();
    }

    #endregion

    private void ShowStatus(string title, string message, InfoBarSeverity severity)
    {
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }

    private static string LastLines(string text, int count) =>
        string.Join(Environment.NewLine, text.Split(Environment.NewLine).TakeLast(count));
}
