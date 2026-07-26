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
                _ = SetBigPictureAsync(!GridShowing);
                return true;

            case BrowserCommand.ToggleImmersion:
                RestartInto(Immersive ? HearthMode.Browse : HearthMode.Immersion);
                return true;

            // Escape is the page's key. Only claim it when a Hearth surface is
            // actually up to receive it; otherwise the page loses its ability to
            // stop a load or dismiss its own dialogs.
            case BrowserCommand.Dismiss:
                if (!GridShowing) return false;
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

    // ------------------------------------------------------------------------
    // The tab grid
    //
    // Sized by the mode rather than by itself (d.grid-inherits-the-mode): it
    // spans rows 1-3 and never touches the window. In browse the tab strip stays
    // above it and it fills the window; in immersion the strip is already gone,
    // so the same markup fills the screen. Up to 0009 it forced the window to
    // maximise, which meant opening the grid resized the browser -- and leaving
    // it did not always put the window back.
    // ------------------------------------------------------------------------

    private bool GridShowing => BigPicture.Visibility == Visibility.Visible;

    /// <summary>Guards the open animation against a second click landing mid-flight.</summary>
    private bool _leavingGrid;

    private void BigPicture_Click(object sender, RoutedEventArgs e) =>
        _ = SetBigPictureAsync(!GridShowing);

    private async Task SetBigPictureAsync(bool on)
    {
        if (_tabs is null || GridShowing == on) return;

        if (on) await EnterGridAsync();
        else await LeaveGridAsync(zoomed: false);
    }

    private async Task EnterGridAsync()
    {
        if (_tabs is null) return;

        await _tabs.SetBigPictureAsync(true);

        _stateBeforeBigPicture = WindowState;
        NavBar.Visibility = Visibility.Collapsed;
        StatusBar.Visibility = Visibility.Collapsed;

        // The tab strip doubles as the title bar, so in browse it stays: losing
        // it would take the window controls with it. Immersion already has it
        // collapsed and leaves it that way.
        if (Immersive) TitleBar.Visibility = Visibility.Collapsed;

        // WebView2 paints above all WPF content (k.wpf-airspace), so the host
        // has to leave the screen rather than sit behind the overlay.
        ContentHost.Visibility = Visibility.Collapsed;
        HidePlaceholder();

        BigPicture.Visibility = Visibility.Visible;
        BigPictureSubtitle.Text = Immersive
            ? $"{_tabs.Tabs.Count} open {Dot} the last few stay awake"
            : $"{_tabs.Tabs.Count} open {Dot} one stays awake while you're here";

        TabWall.SelectedItem = _tabs.Active;
        TabWall.Focus();
        if (TabWall.SelectedItem is not null) TabWall.ScrollIntoView(TabWall.SelectedItem);

        AnimateBigPicture(fadeIn: true);
        StaggerCardsIn();

        Sync();
    }

    /// <param name="activate">
    /// The tab to switch to, if the grid is being left by picking one.
    ///
    /// It is activated HERE, after the shell is restored, and that ordering is
    /// load-bearing rather than tidy — see <see cref="RestoreShell"/>.
    /// </param>
    private async Task LeaveGridAsync(bool zoomed, BrowserTab? activate = null)
    {
        if (_tabs is null) return;

        // A zoom has already carried the eye to the page; fading the wall out
        // again on top of it would read as two separate transitions.
        if (!zoomed)
        {
            AnimateBigPicture(fadeIn: false);
            await Task.Delay(210);
        }

        RestoreShell();

        if (activate is not null) await _tabs.ActivateAsync(activate);

        await _tabs.SetBigPictureAsync(false);

        // Leaving the wall in BROWSE means the user surveyed and chose, so
        // everything else goes back to sleep now rather than waiting for a
        // later activation to push it past the ceiling.
        //
        // Immersion deliberately does not do this. Its whole premise is that the
        // last few tabs in the chain stay warm so stepping between them is
        // instant; evicting them on every visit to the grid would make the grid
        // the most expensive thing in the mode.
        if (!Immersive)
        {
            await _tabs.HibernateAllButActiveAsync();
            AddressBar.Focus();
        }

        Sync();
    }

    /// <summary>
    /// Puts the chrome and the content host back, synchronously.
    ///
    /// THIS MUST RUN BEFORE ANY TAB IS ACTIVATED, and that is not a style
    /// preference. A WebView2 cannot finish initialising inside a collapsed
    /// panel: EnsureCoreWebView2Async waits for a realised HWND and a collapsed
    /// element does not have one, so the call never returns
    /// (k.collapsed-host-blocks-initialisation).
    ///
    /// This is what actually made the grid feel broken. Big Picture pins the
    /// budget to one, so nearly every card in it is a COLD tab; picking one
    /// called ActivateAsync while the host was still collapsed, and it hung
    /// there holding the budget semaphore. Picking a tab that happened to still
    /// hold a controller worked fine, which is exactly why the failure looked
    /// intermittent rather than structural.
    /// </summary>
    private void RestoreShell()
    {
        BigPicture.Visibility = Visibility.Collapsed;
        BigPicture.Opacity = 1;
        ZoomLayer.Children.Clear();
        ContentHost.Visibility = Visibility.Visible;

        NavBar.Visibility = Visibility.Visible;
        StatusBar.Visibility = Visibility.Visible;

        // Immersion has no chrome to restore, and restoring the window state
        // would drop it out of fullscreen.
        if (!Immersive)
        {
            TitleBar.Visibility = Visibility.Visible;
            WindowState = _stateBeforeBigPicture;
        }

        // Force the layout pass now rather than letting it wait for idle. The
        // HwndHost only gets a window once it has been arranged, and the whole
        // point of this method is that the activation which follows depends on
        // that having already happened.
        UpdateLayout();
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

    /// <summary>
    /// Cards rise into place one after another rather than the grid appearing as
    /// a single block. The delay is small and capped: past about a dozen cards a
    /// per-item stagger stops reading as choreography and starts reading as the
    /// application being slow.
    /// </summary>
    private void StaggerCardsIn()
    {
        TabWall.UpdateLayout();

        var ease = new QuarticEase { EasingMode = EasingMode.EaseOut };

        for (var i = 0; i < TabWall.Items.Count; i++)
        {
            if (TabWall.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem card)
                continue;   // Virtualised away, i.e. off-screen. Nothing to stagger.

            var lift = new TranslateTransform(0, 16);
            card.RenderTransform = lift;
            card.Opacity = 0;

            var begin = TimeSpan.FromMilliseconds(Math.Min(i, 12) * 26);

            card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1,
                TimeSpan.FromMilliseconds(240)) { BeginTime = begin, EasingFunction = ease });

            lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(16, 0,
                TimeSpan.FromMilliseconds(320)) { BeginTime = begin, EasingFunction = ease });
        }
    }

    /// <summary>One click opens a card. Selecting it and waiting for a second click is what made the grid feel broken.</summary>
    private void TabCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: BrowserTab tab }) return;
        e.Handled = true;
        _ = OpenFromGridAsync(tab);
    }

    private void TabWall_KeyDown(object sender, KeyEventArgs e)
    {
        if (_tabs is null) return;

        // 1-9 jump straight to a card. In a wall of pictures the position IS the
        // identity, so a number is the most direct thing a keyboard can offer.
        if (e.Key is >= Key.D1 and <= Key.D9 || e.Key is >= Key.NumPad1 and <= Key.NumPad9)
        {
            var index = (e.Key is >= Key.NumPad1 and <= Key.NumPad9 ? e.Key - Key.NumPad1 : e.Key - Key.D1);
            if (_tabs.Tabs.ElementAtOrDefault(index) is { } numbered)
            {
                e.Handled = true;
                _ = OpenFromGridAsync(numbered);
            }
            return;
        }

        if (e.Key is not (Key.Enter or Key.Space)) return;
        e.Handled = true;

        if (TabWall.SelectedItem is BrowserTab selected) _ = OpenFromGridAsync(selected);
    }

    private async Task OpenFromGridAsync(BrowserTab tab)
    {
        if (_tabs is null || _leavingGrid || !GridShowing) return;
        _leavingGrid = true;

        try
        {
            TabWall.SelectedItem = tab;

            // Animate first, activate second. Overlapping them looks tempting --
            // the zoom would cover the page load -- but it deadlocks: the
            // activation cannot complete until the content host is uncollapsed,
            // and it holds the budget semaphore while it waits
            // (k.collapsed-host-blocks-initialisation). LeaveGridAsync restores
            // the shell before it activates, which is the whole ordering.
            await ZoomIntoCardAsync(tab);
            await LeaveGridAsync(zoomed: true, activate: tab);

            Diag.Log($"grid: opened {tab.HostLabel}");
        }
        finally
        {
            _leavingGrid = false;
        }
    }

    /// <summary>
    /// Flies the chosen card up to fill the viewport, so the tab you land on is
    /// visibly the card you picked.
    ///
    /// The card itself cannot be scaled in place: it sits inside the ListBox's
    /// ScrollViewer, which clips to its bounds, so it would be cut off exactly
    /// as the movement got interesting. A VisualBrush copy is painted onto
    /// ZoomLayer, which spans every row, and that is what actually moves.
    /// </summary>
    private async Task ZoomIntoCardAsync(BrowserTab tab)
    {
        const int DurationMs = 280;

        var card = TabWall.ItemContainerGenerator.ContainerFromItem(tab) as ListBoxItem;
        var viewportWidth = BigPicture.ActualWidth;
        var viewportHeight = BigPicture.ActualHeight;

        // Virtualised away, or laid out at zero size. Falling back to the plain
        // fade is correct here: a broken animation must never block the open.
        if (card is null || card.ActualWidth <= 0 || viewportWidth <= 0)
        {
            AnimateBigPicture(fadeIn: false);
            await Task.Delay(200);
            return;
        }

        var origin = card.TransformToVisual(BigPicture).Transform(new Point(0, 0));

        var ghost = new System.Windows.Shapes.Rectangle
        {
            Width = card.ActualWidth,
            Height = card.ActualHeight,
            Fill = new VisualBrush(card) { Stretch = Stretch.None },
            RenderTransformOrigin = new Point(0.5, 0.5)
        };

        var scale = new ScaleTransform(1, 1);
        var slide = new TranslateTransform(0, 0);
        var transform = new TransformGroup();
        transform.Children.Add(scale);
        transform.Children.Add(slide);
        ghost.RenderTransform = transform;

        Canvas.SetLeft(ghost, origin.X);
        Canvas.SetTop(ghost, origin.Y);
        ZoomLayer.Children.Add(ghost);

        // Hide the real card so the copy is not doubled up behind itself.
        card.Opacity = 0;

        // Cover the viewport rather than fit inside it: the card becomes the
        // page, and a letterboxed intermediate step would give that away.
        var target = Math.Max(viewportWidth / card.ActualWidth, viewportHeight / card.ActualHeight);

        var ease = new QuarticEase { EasingMode = EasingMode.EaseIn };
        var duration = TimeSpan.FromMilliseconds(DurationMs);

        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(target, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(target, duration) { EasingFunction = ease });

        slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(
            viewportWidth / 2 - (origin.X + card.ActualWidth / 2), duration) { EasingFunction = ease });
        slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(
            viewportHeight / 2 - (origin.Y + card.ActualHeight / 2), duration) { EasingFunction = ease });

        // The rest of the wall drops away underneath the card that is growing.
        TabWall.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });

        await Task.Delay(DurationMs);

        // Fade the ghost out at the very end so the swap to live content is not
        // a hard cut from a screenshot to a page.
        ghost.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(110)));
        BigPicture.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(110)));

        await Task.Delay(110);

        card.Opacity = 1;
        TabWall.BeginAnimation(OpacityProperty, null);
        TabWall.Opacity = 1;
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
