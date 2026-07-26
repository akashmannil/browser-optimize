using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hearth.Core;

namespace Hearth.Views;

public partial class MainWindow : Window
{
    private const string HomeUrl = "https://example.com";

    // Separators and punctuation in this file stay ASCII on purpose. A
    // PowerShell round-trip over this source once re-encoded every non-ASCII
    // character (PS 5.1 reads BOM-less UTF-8 as ANSI), and the damage showed up
    // in the shipped UI as "6 open A. one stays awake". Anything user-visible
    // that needs real typography is set in XAML, which is read as UTF-8.
    private const string Dot = "·";   // middle dot
    private const string Dash = "—";  // em dash
    private const string Shield = "🛡"; // shield, for the blocked-content pill

    private TabManager? _tabs;
    private ShortcutRouter? _router;
    private DispatcherTimer? _memoryTimer;
    private long _peakBytes;
    private WindowState _stateBeforeBigPicture = WindowState.Normal;

    private readonly SessionStore _session = new();

    /// <summary>Set while restarting, so the exit handler does not fight it.</summary>
    private bool _restarting;

    private bool Immersive => App.Mode == HearthMode.Immersion;

    /// <summary>Guards against SelectionChanged firing while we set the selection.</summary>
    private bool _syncing;

    public MainWindow()
    {
        InitializeComponent();
        // HEARTH_THEME pins the theme for screenshots and comparison runs.
        // Defaults to following Windows, which is what a browser should do.
        ThemeManager.Apply(Environment.GetEnvironmentVariable("HEARTH_THEME") switch
        {
            "light" or "Light" => AppTheme.Light,
            "dark" or "Dark" => AppTheme.Dark,
            _ => AppTheme.System
        });
        ThemeManager.WatchSystem();

        // Custom chrome maximises over the taskbar unless WM_GETMINMAXINFO is
        // answered with the work area. Without this the bottom row of the
        // layout, the memory readout, disappears when maximised.
        MaximiseFix.Attach(this);

        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += (_, _) => UpdateMaximiseGlyph();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // A mode switch relaunches the process, and the outgoing instance still
        // holds the WebView2 user-data folder. Creating the environment before
        // it lets go fails outright, so wait for the handover first.
        App.AwaitHandover();

        _tabs = new TabManager(ContentHost, HearthOptions.FromEnvironment(App.Mode));
        _tabs.Changed += (_, _) => Dispatcher.Invoke(Sync);
        _tabs.Rehydrating += (_, tab) => Dispatcher.Invoke(() => ShowPlaceholder(tab));
        _tabs.Rehydrated += (_, _) => Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(220);
            HidePlaceholder();
        });

        // Every new renderer gets the keyboard hook. Attaching here rather than
        // once at startup is not an optimisation -- a tab torn down by the
        // budget and later rebuilt gets a brand-new control, and a hook attached
        // only to the original would leave shortcuts dead on exactly the tabs
        // that hibernation touched (k.two-keyboard-paths).
        _router = new ShortcutRouter(Execute);
        _tabs.Realised += (_, tab) =>
        {
            if (tab.View is { } view) _router.Attach(view);
        };
        _tabs.Gesture += (_, gesture) => Dispatcher.Invoke(() => OnGesture(gesture));

        TabStrip.ItemsSource = _tabs.Tabs;
        TabWall.ItemsSource = _tabs.Tabs;

        ApplyMode();

        var startup = App.StartupUrls();

        // A saved session takes precedence over the default home page but not
        // over URLs asked for explicitly on the command line.
        if (startup.Length == 0 && _session.Load() is { } saved)
        {
            var active = _tabs.RestoreSession(saved);

            // Consume the file immediately. If restoring is what crashes us, a
            // session left on disk would reopen the same tabs on every launch
            // and the browser would never start again.
            _session.Clear();

            if (active is not null) await _tabs.ActivateAsync(active);
        }
        else if (startup.Length == 0)
        {
            _tabs.Open(HomeUrl);
        }
        else if (Environment.GetEnvironmentVariable("HEARTH_ACTIVATE_ALL") is "1" or "true")
        {
            foreach (var url in startup)
            {
                var tab = _tabs.Open(url, activate: false);
                await _tabs.ActivateAsync(tab);

                // Dwell until the page has painted. ActivateAsync returns once
                // the renderer exists, not once the document has loaded, so
                // advancing immediately would blur every tab before first paint
                // and produce no snapshots at all.
                for (var waited = 0; waited < 80 && !tab.HasRendered; waited++)
                    await Task.Delay(100);
            }
        }
        else
        {
            for (var i = 0; i < startup.Length; i++)
                _tabs.Open(startup[i], activate: i == startup.Length - 1);
        }

        StartMemorySampling();
        UpdateMaximiseGlyph();

        var version = await _tabs.GetRuntimeVersionAsync();
        Title = Immersive
            ? $"Hearth {Dash} immersion {Dash} WebView2 {version}"
            : $"Hearth {Dash} WebView2 {version}";

        // The keyboard hook is the one thing here that reaches into the SDK's
        // internals, so a failure is reported rather than left to be discovered
        // as "shortcuts sometimes do nothing".
        if (_router.NativeHookError is { } hookError)
            Debug.WriteLine($"[hearth] page-level shortcuts unavailable: {hookError}");

        Sync();
    }

    // ------------------------------------------------------------------------
    // Mode
    // ------------------------------------------------------------------------

    /// <summary>
    /// Puts the window into the shape the current mode calls for. Called once at
    /// startup, because mode is fixed for the lifetime of the process
    /// (k.browser-args-fixed-at-creation) -- there is no live transition to
    /// animate, which is exactly what makes this simple.
    /// </summary>
    private void ApplyMode()
    {
        ImmersionButton.ToolTip = Immersive
            ? "Back to browsing  ·  restarts Hearth  ·  F11"
            : "Immersion  ·  restarts Hearth into fullscreen  ·  F11";
        ImmersionButton.Content = Immersive ? "" : "";

        if (!Immersive) return;

        // Device fullscreen: no chrome at all, and the taskbar is covered. 0007
        // deliberately prevented that because it hid the memory readout; here the
        // readout is intentionally gone, so there is nothing left to protect.
        TitleBar.Visibility = Visibility.Collapsed;
        NavBar.Visibility = Visibility.Collapsed;
        StatusBar.Visibility = Visibility.Collapsed;

        // Explicitly sized to the monitor rather than maximised. A maximised
        // window measures the same and still has the taskbar drawn over it --
        // see k.maximised-is-not-fullscreen, which cost a couple of wrong fixes
        // before the child-window geometry showed the app had been right all
        // along and the shell was simply declining to yield.
        ResizeMode = ResizeMode.NoResize;
        MaximiseFix.Fullscreen(this);

        // Topmost is what makes the shell yield the taskbar, but a topmost
        // window that stays on top after you alt-tab away is a trap rather than
        // a feature. Yield it while another app is in front, and take it back on
        // return.
        Activated += (_, _) => Topmost = true;
        Deactivated += (_, _) => Topmost = false;
    }

    /// <summary>
    /// Leaves this mode for the other one, by relaunching. The session is
    /// written first: everything in memory is about to be discarded, and losing
    /// a user's tabs because they changed a setting would make the setting
    /// unusable.
    /// </summary>
    private void RestartInto(HearthMode mode)
    {
        if (_tabs is null || _restarting) return;
        _restarting = true;

        _session.Save(_tabs.CaptureSession());
        App.RestartInto(mode);
    }

    private void Immersion_Click(object sender, RoutedEventArgs e) =>
        RestartInto(Immersive ? HearthMode.Browse : HearthMode.Immersion);

    /// <summary>
    /// Tabs are saved on the way out so an ordinary quit restores like a mode
    /// switch does. A restart has already written a better session than this one
    /// (it knows which tab was active at the moment of the switch), so it skips.
    /// </summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_restarting || _tabs is null) return;
        _session.Save(_tabs.CaptureSession());
    }

    // ------------------------------------------------------------------------
    // Gestures
    //
    // These arrive as web messages from an injected listener, because mouse
    // input over a page never reaches WPF (k.mouse-input-never-reaches-wpf).
    // ------------------------------------------------------------------------

    private void OnGesture(string gesture)
    {
        Diag.Log($"gesture {gesture}");

        switch (gesture)
        {
            case "tab-next": Execute(BrowserCommand.NextTab, 0); break;
            case "tab-prev": Execute(BrowserCommand.PreviousTab, 0); break;
            case "back": Execute(BrowserCommand.Back, 0); break;
            case "forward": Execute(BrowserCommand.Forward, 0); break;
        }
    }

    // ------------------------------------------------------------------------
    // Commands
    //
    // Every keyboard-reachable action lands here, from both the chrome path and
    // the page path. Returning false declines the key and lets it through to
    // the page, which is what keeps Escape and the zoom keys well-behaved.
    // ------------------------------------------------------------------------

    private bool Execute(BrowserCommand command, int index)
    {
        if (_tabs is null) return false;

        switch (command)
        {
            case BrowserCommand.NewTab:
                NewTab();
                return true;

            case BrowserCommand.CloseTab:
                if (_tabs.Active is { } closing) _tabs.Close(closing);
                return true;

            case BrowserCommand.ReopenClosedTab:
                return _tabs.ReopenLastClosed() is not null;

            case BrowserCommand.FocusAddress:
                FocusAddress();
                return true;

            case BrowserCommand.Reload:
                _tabs.Active?.View?.CoreWebView2?.Reload();
                return true;

            case BrowserCommand.HardReload:
                HardReload();
                return true;

            case BrowserCommand.Back:
                Back_Click(this, new RoutedEventArgs());
                return true;

            case BrowserCommand.Forward:
                Forward_Click(this, new RoutedEventArgs());
                return true;

            case BrowserCommand.Home:
                Home_Click(this, new RoutedEventArgs());
                return true;

            case BrowserCommand.NextTab:
                return StepTab(+1);

            case BrowserCommand.PreviousTab:
                return StepTab(-1);

            case BrowserCommand.SelectTab:
                return SelectTab(_tabs.Tabs.ElementAtOrDefault(index));

            case BrowserCommand.SelectLastTab:
                return SelectTab(_tabs.Tabs.LastOrDefault());

            case BrowserCommand.ZoomIn:
                return Zoom(+1);

            case BrowserCommand.ZoomOut:
                return Zoom(-1);

            case BrowserCommand.ZoomReset:
                return Zoom(0);

            case BrowserCommand.ToggleGrid:
                _ = SetBigPictureAsync(BigPicture.Visibility != Visibility.Visible);
                return true;

            case BrowserCommand.ToggleImmersion:
                RestartInto(Immersive ? HearthMode.Browse : HearthMode.Immersion);
                return true;

            // Escape is the page's key. Only claim it when a Hearth surface is
            // actually up to receive it; otherwise the page loses its ability to
            // stop a load or dismiss its own dialogs.
            case BrowserCommand.Dismiss:
                if (BigPicture.Visibility != Visibility.Visible) return false;
                _ = SetBigPictureAsync(false);
                return true;

            default:
                return false;
        }
    }

    private bool StepTab(int delta)
    {
        if (_tabs is null || _tabs.Tabs.Count < 2) return false;

        var current = _tabs.Active is { } active ? _tabs.Tabs.IndexOf(active) : 0;
        var count = _tabs.Tabs.Count;

        // Wrap rather than clamp: Ctrl+Tab off the end of the strip returning to
        // the first tab is what every browser does, and stopping dead reads as
        // the shortcut having failed.
        var next = ((current + delta) % count + count) % count;
        return SelectTab(_tabs.Tabs[next]);
    }

    private bool SelectTab(BrowserTab? tab)
    {
        if (_tabs is null || tab is null) return false;
        if (ReferenceEquals(tab, _tabs.Active)) return true;

        _ = _tabs.ActivateAsync(tab);
        return true;
    }

    /// <summary>
    /// Chrome's zoom ladder. Steps rather than a multiplier, because repeated
    /// multiplication lands on values like 1.7280000000000002 and the readout
    /// has to apologise for them.
    /// </summary>
    private static readonly double[] ZoomLadder =
        [0.25, 0.33, 0.5, 0.67, 0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0];

    private bool Zoom(int direction)
    {
        if (_tabs?.Active?.View is not { } view) return false;

        if (direction == 0)
        {
            view.ZoomFactor = 1.0;
            Sync();
            return true;
        }

        var current = view.ZoomFactor;
        var nearest = Array.FindIndex(ZoomLadder, z => z >= current - 0.001);
        if (nearest < 0) nearest = ZoomLadder.Length - 1;

        var target = Math.Clamp(nearest + direction, 0, ZoomLadder.Length - 1);
        view.ZoomFactor = ZoomLadder[target];
        Sync();
        return true;
    }

    /// <summary>
    /// Reload ignoring cache. CoreWebView2.Reload() honours the cache, so the
    /// shortcut users press specifically to defeat a stale asset would not.
    /// </summary>
    private void HardReload()
    {
        if (_tabs?.Active?.View?.CoreWebView2 is not { } core) return;

        try
        {
            _ = core.CallDevToolsProtocolMethodAsync(
                "Page.reload", "{\"ignoreCache\":true}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[hearth] hard reload failed, falling back: {ex.Message}");
            core.Reload();
        }
    }

    private void FocusAddress()
    {
        AddressBar.Focus();
        AddressBar.SelectAll();
    }

    private void NewTab()
    {
        _tabs?.Open(HomeUrl);
        FocusAddress();
    }

    // Window chrome ----------------------------------------------------------

    private void Minimise_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void Maximise_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaximiseGlyph() =>
        // E923 restore-down, E922 maximise: the same glyphs Windows uses, so the
        // control keeps meaning what people expect it to mean.
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        var next = ThemeManager.Cycle();
        ThemeButton.ToolTip = $"Theme: {next}";
    }

    // Sync -------------------------------------------------------------------

    private void Sync()
    {
        if (_tabs is null) return;

        var active = _tabs.Active;
        if (active is not null && !AddressBar.IsFocused)
            AddressBar.Text = active.Url;

        _syncing = true;
        if (!ReferenceEquals(TabStrip.SelectedItem, active)) TabStrip.SelectedItem = active;
        _syncing = false;

        var core = active?.View?.CoreWebView2;
        BackButton.IsEnabled = core?.CanGoBack ?? false;
        ForwardButton.IsEnabled = core?.CanGoForward ?? false;
        ReloadButton.IsEnabled = core is not null;

        SyncShield(active);

        // "Awake" and "resting", not "live" and "hibernated". Resting carries
        // the promise that matters: it will be there when you come back.
        var resting = _tabs.Tabs.Count - _tabs.LiveCount;
        var zoom = active?.View?.ZoomFactor ?? 1.0;

        TabsText.Text = (resting > 0
            ? $"{_tabs.LiveCount} awake {Dot} {resting} resting"
            : $"{_tabs.LiveCount} awake")
            + (Math.Abs(zoom - 1.0) > 0.001 ? $" {Dot} {zoom * 100:0}%" : string.Empty);
    }

    /// <summary>
    /// The shield appears only when this page actually lost something. A badge
    /// that is always on is wallpaper; one that appears exactly when a login
    /// button stops responding is the explanation the user needs at that moment.
    /// </summary>
    private void SyncShield(BrowserTab? active)
    {
        if (active is null || active.BlockedCount == 0)
        {
            ShieldButton.Visibility = Visibility.Collapsed;
            return;
        }

        ShieldButton.Visibility = Visibility.Visible;
        ShieldButton.Content = $"{Shield} {active.BlockedCount}";
        ShieldButton.ToolTip =
            $"{active.BlockedCount} embedded frames and media requests were skipped on "
            + $"{active.HostLabel}, which is how this page costs one renderer instead of "
            + $"several.\n\nIf a login, payment or video is missing, click to load "
            + $"{active.HostLabel} in full from now on.";
    }

    private void Shield_Click(object sender, RoutedEventArgs e) => _tabs?.AllowActiveSite();

    private void StartMemorySampling()
    {
        _memoryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _memoryTimer.Tick += (_, _) =>
        {
            var reading = MemoryProbe.Sample();
            _peakBytes = Math.Max(_peakBytes, reading.TotalBytes);

            MemoryText.Text = MemoryProbe.Format(reading.TotalBytes);

            var saved = _peakBytes - reading.TotalBytes;
            SavedText.Text = saved > 32 * 1024 * 1024
                ? $"{MemoryProbe.Format(saved)} returned since this session's peak"
                : $"{reading.ProcessCount} processes";
        };
        _memoryTimer.Start();
    }

    // Placeholder ------------------------------------------------------------

    private void ShowPlaceholder(BrowserTab tab)
    {
        if (_tabs?.Snapshots.Get(tab.Id) is not { } snap || !File.Exists(snap.ImagePath))
        {
            HidePlaceholder();
            return;
        }

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(snap.ImagePath);
            bmp.EndInit();
            bmp.Freeze();

            Placeholder.Source = bmp;
            Placeholder.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[hearth] placeholder load failed: {ex.Message}");
            HidePlaceholder();
        }
    }

    private void HidePlaceholder()
    {
        Placeholder.Visibility = Visibility.Collapsed;
        Placeholder.Source = null;
    }

    // Navigation -------------------------------------------------------------

    private void Navigate()
    {
        if (_tabs?.Active is not { } active) return;

        var raw = AddressBar.Text.Trim();
        if (raw.Length == 0) return;

        var url = raw.Contains("://")
            ? raw
            : raw.Contains('.') && !raw.Contains(' ')
                ? "https://" + raw
                : "https://duckduckgo.com/?q=" + Uri.EscapeDataString(raw);

        _tabs.Navigate(active, url);
    }

    private void AddressBar_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Navigate();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        var core = _tabs?.Active?.View?.CoreWebView2;
        if (core?.CanGoBack == true) core.GoBack();
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        var core = _tabs?.Active?.View?.CoreWebView2;
        if (core?.CanGoForward == true) core.GoForward();
    }

    private void Reload_Click(object sender, RoutedEventArgs e) =>
        _tabs?.Active?.View?.CoreWebView2?.Reload();

    private void Home_Click(object sender, RoutedEventArgs e)
    {
        if (_tabs?.Active is { } tab) _tabs.Navigate(tab, HomeUrl);
    }

    private void NewTab_Click(object sender, RoutedEventArgs e) => NewTab();

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: BrowserTab tab }) _tabs?.Close(tab);
        e.Handled = true;
    }

    private void TabStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _tabs is null) return;
        if (TabStrip.SelectedItem is BrowserTab tab && !ReferenceEquals(tab, _tabs.Active))
            _ = _tabs.ActivateAsync(tab);
    }

    // Big Picture ------------------------------------------------------------

    private void BigPicture_Click(object sender, RoutedEventArgs e) =>
        _ = SetBigPictureAsync(BigPicture.Visibility != Visibility.Visible);

    private async Task SetBigPictureAsync(bool on)
    {
        if (_tabs is null) return;
        if ((BigPicture.Visibility == Visibility.Visible) == on) return;

        if (on)
        {
            await _tabs.SetBigPictureAsync(true);

            // Genuinely fullscreen rather than an overlay: the chrome rows are
            // collapsed and the window maximises, so the wall owns the screen
            // the way a lean-back interface should.
            _stateBeforeBigPicture = WindowState;
            TitleBar.Visibility = Visibility.Collapsed;
            NavBar.Visibility = Visibility.Collapsed;
            StatusBar.Visibility = Visibility.Collapsed;
            if (!Immersive) WindowState = WindowState.Maximized;

            // WebView2 paints above all WPF content (k.wpf-airspace), so the
            // host has to leave the screen rather than sit behind the overlay.
            ContentHost.Visibility = Visibility.Collapsed;
            HidePlaceholder();

            BigPicture.Visibility = Visibility.Visible;
            BigPictureSubtitle.Text =
                $"{_tabs.Tabs.Count} open {Dot} one stays awake while you're here";

            TabWall.SelectedItem = _tabs.Active;
            TabWall.Focus();
            if (TabWall.SelectedItem is not null) TabWall.ScrollIntoView(TabWall.SelectedItem);

            AnimateBigPicture(fadeIn: true);
        }
        else
        {
            await AnimateBigPictureOutAsync();

            BigPicture.Visibility = Visibility.Collapsed;
            ContentHost.Visibility = Visibility.Visible;

            // Immersion has no chrome to restore, and restoring the window state
            // would drop it out of fullscreen.
            if (!Immersive)
            {
                TitleBar.Visibility = Visibility.Visible;
                NavBar.Visibility = Visibility.Visible;
                StatusBar.Visibility = Visibility.Visible;
                WindowState = _stateBeforeBigPicture;
            }

            await _tabs.SetBigPictureAsync(false);

            // Leaving the wall in BROWSE means the user surveyed and chose, so
            // everything else goes back to sleep now rather than waiting for a
            // later activation to push it past the ceiling.
            //
            // Immersion deliberately does not do this. Its whole premise is that
            // the last few tabs in the chain stay warm so stepping between them
            // is instant; evicting them on every visit to the grid would make the
            // grid the most expensive thing in the mode.
            if (!Immersive)
            {
                await _tabs.HibernateAllButActiveAsync();
                AddressBar.Focus();
            }
        }

        Sync();
    }

    /// <summary>
    /// Fades and scales the wall in. Starting slightly small and settling to
    /// full size reads as the surface arriving; a plain fade reads as a dialog
    /// appearing.
    /// </summary>
    private void AnimateBigPicture(bool fadeIn)
    {
        var ease = new QuarticEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(300);

        BigPictureScale.ScaleX = BigPictureScale.ScaleY = fadeIn ? 0.965 : 1;

        BigPicture.BeginAnimation(OpacityProperty,
            new DoubleAnimation(fadeIn ? 0 : 1, fadeIn ? 1 : 0, duration) { EasingFunction = ease });

        var to = fadeIn ? 1 : 0.965;
        BigPictureScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(to, duration) { EasingFunction = ease });
        BigPictureScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(to, duration) { EasingFunction = ease });
    }

    private async Task AnimateBigPictureOutAsync()
    {
        AnimateBigPicture(fadeIn: false);
        await Task.Delay(210);
    }

    private async void TabWall_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space)) return;
        e.Handled = true;
        await OpenSelectedAsync();
    }

    private async void TabWall_Open(object sender, MouseButtonEventArgs e) =>
        await OpenSelectedAsync();

    private async Task OpenSelectedAsync()
    {
        if (_tabs is null || TabWall.SelectedItem is not BrowserTab tab) return;
        await _tabs.ActivateAsync(tab);
        await SetBigPictureAsync(false);
    }

    /// <summary>
    /// The chrome-focus half of the keyboard story. This fires only while focus
    /// is on WPF -- the address bar, the tab strip, the grid. The moment focus
    /// enters a page it stops firing entirely, which is why the router also
    /// hooks each WebView2 directly (k.two-keyboard-paths).
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_router is null) return;
        if (_router.HandleWpfKey(e)) e.Handled = true;
    }
}
