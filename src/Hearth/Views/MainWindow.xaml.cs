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
    private const string HomeUrl = "https://www.google.com";

    // EVERY non-ASCII character in this file is a \u escape, and that is a
    // rule rather than a preference (k.non-ascii-literals-do-not-survive-tooling).
    //
    // This source has now lost non-ASCII content twice, by two unrelated
    // mechanisms. A PowerShell round-trip re-encoded it, because PS 5.1 reads
    // BOM-less UTF-8 as ANSI. Later a read-then-rewrite dropped the private-use
    // glyphs entirely, because tools that display source render them as nothing,
    // so they came back as empty strings. The second one shipped: the maximise
    // button was an empty string from 0008 until 0012, leaving the window with
    // no restore control at all.
    //
    // Escapes are pure ASCII on disk, so nothing in the chain can lose them, and
    // they state which code point is meant instead of relying on a glyph render.
    private const string Dot = "\u00B7";              // middle dot
    private const string Dash = "\u2014";             // em dash
    private const string Shield = "\uD83D\uDEE1";      // shield, blocked-content pill

    // Segoe MDL2 Assets - the glyphs Windows own caption buttons use.
    private const string GlyphMaximise = "\uE922";    // ChromeMaximize
    private const string GlyphRestore = "\uE923";     // ChromeRestore

    private TabManager? _tabs;
    private ShortcutRouter? _router;
    private DispatcherTimer? _memoryTimer;
    private long _peakBytes;
    private WindowState _stateBeforeBigPicture = WindowState.Normal;

    /// <summary>Width the immersion chip's label needs once revealed.</summary>
    private double _immersionLabelWidth;

    /// <summary>False until the travelling tab indicator has a position to move FROM.</summary>
    private bool _indicatorPlaced;

    /// <summary>Where the indicator is heading, so a repeat request can be dropped.</summary>
    private double _indicatorX;
    private double _indicatorWidth;
    private bool _indicatorQueued;
    private bool _indicatorRetried;

    /// <summary>Tabs playing their closing animation, so a second press is ignored.</summary>
    private readonly HashSet<BrowserTab> _closingTabs = [];

    private readonly SessionStore _session = new();

    /// <summary>Set while restarting, so the exit handler does not fight it.</summary>
    private bool _restarting;

    private bool Immersive => App.Mode == HearthMode.Immersion;

    /// <summary>Guards against SelectionChanged firing while we set the selection.</summary>
    private bool _syncing;

    /// <summary>The tab whose URL the address bar is currently showing.</summary>
    private BrowserTab? _addressBarTab;

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

        // The chrome starts invisible so the startup curtain has something to
        // hand over TO. Set here rather than in XAML deliberately: if the reveal
        // never runs -- an exception during startup, a mode that never calls it
        // -- a window with Opacity="0" baked into the markup would be a browser
        // with no controls and no way to find out why. Every path that hides it
        // is paired with a finally that puts it back.
        //
        // Immersion is exempt: its chrome is not in this window's layout at all,
        // it has been lifted into the EdgeBar windows, and those manage their
        // own opacity. Dimming it here would leave the edge bars permanently
        // blank.
        if (Motion.Enabled && !Immersive)
            foreach (var part in Chrome) part.Opacity = 0;

        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += (_, _) => UpdateMaximiseGlyph();
        SizeChanged += (_, _) => UpdateTabIndicator(animate: false);
    }

    /// <summary>The three strips that make up the browser frame, top to bottom.</summary>
    private UIElement[] Chrome => [TitleBar, NavBar, StatusBar];

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // The curtain goes up FIRST, before the handover wait and before the
        // environment exists. Everything after this line can take seconds --
        // waiting for the previous process to die, creating the browser process,
        // loading a page -- and all of it used to happen in full view, as an
        // empty grey window that looked hung (d.startup-is-a-transition).
        var veil = Veil.Cover(this, Immersive ? "Immersion" : null);
        var startupClock = Stopwatch.StartNew();

        // A mode switch relaunches the process, and the outgoing instance still
        // holds the WebView2 user-data folder. Creating the environment before
        // it lets go fails outright, so wait for the handover first.
        await App.AwaitHandoverAsync();

        _tabs = new TabManager(ContentHost, HearthOptions.FromEnvironment(App.Mode));
        _tabs.Changed += (_, _) => Dispatcher.Invoke(Sync);
        // The three beats of a rebuild, in order: the snapshot goes up, the page
        // loads behind it, the live control replaces it. Before 0015 the middle
        // beat did not exist and the first two ran back to back, which is why
        // the snapshot was never on screen (k.the-placeholder-was-never-visible).
        _tabs.Rehydrating += (_, tab) => Dispatcher.Invoke(() => ShowPlaceholder(tab));
        _tabs.RevealGate = HoldPlaceholderUntilPaintedAsync;
        _tabs.Rehydrated += (_, _) => Dispatcher.Invoke(HidePlaceholder);

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

        // A chip that simply exists on the next frame is the one place the tab
        // strip still cut rather than moved. Opening a tab is the most frequent
        // structural change in a browser, so it is worth animating even though
        // the chip is small.
        ((System.Collections.Specialized.INotifyCollectionChanged)_tabs.Tabs)
            .CollectionChanged += OnTabsChanged;

        ApplyMode();
        MeasureImmersionChip();

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

            // NOT awaited. A restored tab is cold and has a snapshot, so
            // activating it goes through the reveal gate, which waits for the
            // page to paint -- awaiting that here would put a page load between
            // the window opening and the curtain being allowed to consider
            // coming down. It measured as a nine-second startup curtain, and
            // the fix is to let the two run alongside each other, which is what
            // the curtain is for.
            if (active is not null) _ = _tabs.ActivateAsync(active);
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

        // The runtime version is a developer detail and belongs in the log, not
        // in the taskbar. It was in the title because the title had never been
        // designed; a browser's title is the page you are on.
        Diag.Log($"WebView2 runtime {await _tabs.GetRuntimeVersionAsync()}");

        // The keyboard hook is the one thing here that reaches into the SDK's
        // internals, so a failure is reported rather than left to be discovered
        // as "shortcuts sometimes do nothing".
        if (_router.NativeHookError is { } hookError)
            Debug.WriteLine($"[hearth] page-level shortcuts unavailable: {hookError}");

        Sync();

        await FinishStartupAsync(veil, startupClock);
    }

    // ------------------------------------------------------------------------
    // Startup
    // ------------------------------------------------------------------------

    /// <summary>
    /// Lowers the startup curtain and brings the browser in behind it.
    ///
    /// The curtain is held until the first page has actually painted rather than
    /// for a fixed beat, for the same reason the rehydration placeholder is
    /// (0013): a timed splash is wrong in both directions. Too short and the
    /// curtain drops onto a blank content area, which is a worse first frame
    /// than the curtain itself; too long and the browser is deliberately slower
    /// than it needs to be.
    ///
    /// There is a floor as well as a ceiling. A curtain that appears and leaves
    /// inside 200 ms is a flash, not a transition, and reads as a glitch --
    /// warm-start Hearth reaches first paint quickly enough for that to happen.
    /// </summary>
    private async Task FinishStartupAsync(Veil veil, Stopwatch clock)
    {
        try
        {
            // The clock started when the window loaded, not when this method
            // was reached, so the ceiling caps the time the USER waits rather
            // than the time this method waits.
            var ceiling = TimeSpan.FromMilliseconds(2600);
            var floor = TimeSpan.FromMilliseconds(Motion.Enabled ? 700 : 0);

            // The benchmark path deliberately activates every startup URL in
            // turn and can run for a minute; nobody is watching it, and leaving
            // a curtain over the run would also cover its screenshots.
            var benchmarking =
                Environment.GetEnvironmentVariable("HEARTH_ACTIVATE_ALL") is "1" or "true";

            if (!benchmarking)
            {
                // HasContent, not HasRendered. The stricter signal waits for
                // every subresource on the page, and measured against
                // google.com it fires roughly a second after the layout a
                // person would call loaded -- so the curtain was routinely
                // timing out on a page that had been readable for a while
                // (k.navigation-completed-is-not-first-paint).
                while (clock.Elapsed < ceiling && _tabs?.Active?.HasContent != true)
                    await Task.Delay(50);

                if (floor - clock.Elapsed is { Ticks: > 0 } remaining)
                    await Task.Delay(remaining);
            }

            Diag.Log($"startup: curtain held {clock.ElapsedMilliseconds} ms, "
                   + $"content={_tabs?.Active?.HasContent == true}");

            await veil.DismissAsync();

            await RevealChromeAsync();
        }
        catch (Exception ex)
        {
            // Nothing about a transition is worth losing the browser over.
            Diag.Log($"startup reveal failed: {ex.Message}");
            veil.Release();
        }
        finally
        {
            foreach (var part in Chrome)
            {
                part.BeginAnimation(OpacityProperty, null);
                part.Opacity = 1;
            }
        }
    }

    /// <summary>
    /// The browser arrives from behind the curtain: the two top strips drop in
    /// from above and the status bar rises from below, each fading as it moves,
    /// so the frame assembles around the page rather than switching on.
    ///
    /// The page itself does not participate, and cannot: WebView2 is a child
    /// HWND that ignores WPF opacity entirely (k.wpf-airspace). This is the same
    /// reason the theme switch fades only the chrome.
    /// </summary>
    private async Task RevealChromeAsync()
    {
        // In immersion the strips live in the EdgeBar windows and are concealed
        // by design -- there is no frame to assemble.
        if (Immersive || !Motion.Enabled) return;

        using var meter = FrameMeter.Start("chrome-reveal");

        var arrivals = new (FrameworkElement Part, double FromY, TimeSpan Delay)[]
        {
            (TitleBar, -14, TimeSpan.Zero),
            (NavBar, -10, TimeSpan.FromMilliseconds(60)),
            (StatusBar, 12, TimeSpan.FromMilliseconds(40))
        };

        var running = new List<Task>();

        foreach (var (part, fromY, delay) in arrivals)
        {
            var slide = new TranslateTransform(0, fromY);
            part.RenderTransform = slide;

            part.BeginAnimation(OpacityProperty,
                Motion.From(0, 1, Motion.Slow, Motion.Enter, delay));

            running.Add(Motion.RunAsync(slide, TranslateTransform.YProperty,
                Motion.From(fromY, 0, Motion.Slow, Motion.Emphasis, delay)));
        }

        await Task.WhenAll(running);

        foreach (var (part, _, _) in arrivals) part.RenderTransform = Transform.Identity;
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
        // The label says which way the button goes; the tooltip says what it
        // costs. A mode change that relaunches the process should never be a
        // surprise, and a bare icon cannot carry that warning.
        ImmersionLabel.Text = Immersive ? "Exit immersion" : "Immersion";
        ImmersionButton.ToolTip = Immersive
            ? $"Leave immersion and restart Hearth  {Dot}  F11"
            : $"Restart Hearth into fullscreen immersion  {Dot}  F11";

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

        BuildEdgeChrome();

        // Topmost is what makes the shell yield the taskbar, but a topmost
        // window that stays on top after you alt-tab away is a trap rather than
        // a feature. Yield it while another app is in front, and take it back on
        // return.
        Activated += (_, _) => Topmost = true;
        Deactivated += (_, _) =>
        {
            Topmost = false;
            ConcealEdges();
        };
    }

    // ------------------------------------------------------------------------
    // Immersion edge chrome
    //
    // The controls are not duplicated for this mode. The real tab strip,
    // navigation bar and status bar are LIFTED OUT of the main window's layout
    // and re-hosted in EdgeBar windows, so there is one address bar, one shield,
    // one memory readout, and no second copy to keep in sync.
    // ------------------------------------------------------------------------

    private EdgeBar? _topEdge;
    private EdgeBar? _bottomEdge;

    /// <summary>False while the grid is up, so the edges stay out of its way.</summary>
    private bool _edgesEnabled = true;

    private void BuildEdgeChrome()
    {
        // Detach from the main grid first. These stay Visible: in immersion
        // their visibility is the EdgeBar's business, not the layout's.
        Root.Children.Remove(TitleBar);
        Root.Children.Remove(NavBar);
        Root.Children.Remove(StatusBar);

        TitleBar.Visibility = Visibility.Visible;
        NavBar.Visibility = Visibility.Visible;
        StatusBar.Visibility = Visibility.Visible;
        NavBar.Margin = new Thickness(10, 0, 10, 8);

        var top = new StackPanel { Background = (Brush)FindResource("Bg") };
        top.Children.Add(TitleBar);
        top.Children.Add(NavBar);

        _topEdge = new EdgeBar(this, EdgeBar.Side.Top, top, 96);
        _bottomEdge = new EdgeBar(this, EdgeBar.Side.Bottom, StatusBar, 34);
    }

    private void OnEdge(string edge)
    {
        if (!Immersive || !_edgesEnabled) return;

        switch (edge)
        {
            case "top":
                _topEdge?.Reveal();
                _bottomEdge?.Conceal();
                break;

            case "bottom":
                _bottomEdge?.Reveal();
                _topEdge?.Conceal();
                break;

            default:
                ConcealEdges();
                break;
        }
    }

    private void ConcealEdges()
    {
        _topEdge?.Conceal();
        _bottomEdge?.Conceal();
    }

    /// <summary>
    /// Leaves this mode for the other one, by relaunching. The session is
    /// written first: everything in memory is about to be discarded, and losing
    /// a user's tabs because they changed a setting would make the setting
    /// unusable.
    ///
    /// The curtain goes up before the relaunch, and the incoming process raises
    /// the same curtain the moment its window exists, so a mode change reads as
    /// ONE transition rather than an application vanishing and a different one
    /// appearing (d.the-restart-is-a-transition). It is the same mark either
    /// side of the process boundary, which is the only continuity available:
    /// nothing else survives, by construction.
    /// </summary>
    private async void RestartInto(HearthMode mode)
    {
        if (_tabs is null || _restarting) return;
        _restarting = true;

        _session.Save(_tabs.CaptureSession());

        var veil = Veil.Cover(this, mode == HearthMode.Immersion
            ? "Restarting into immersion"
            : "Returning to browsing");

        // The successor starts NOW, under the curtain, and spends its own
        // startup in parallel with this animation rather than after it.
        if (!App.StartSuccessor(mode))
        {
            veil.Release();
            _restarting = false;
            return;
        }

        // Then wait for the curtain to be fully up before exiting. Dying
        // underneath a half-faded one puts the seam in the middle of the
        // animation, which is the most visible place it could be.
        await Task.Delay(Motion.Wait(Motion.Slow));

        App.HandOver();
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

            case "edge-top": OnEdge("top"); break;
            case "edge-bottom": OnEdge("bottom"); break;
            case "edge-none": OnEdge("none"); break;
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
                if (_tabs.Active is { } closing) _ = CloseTabAsync(closing);
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
        // The same glyphs Windows own caption buttons use, so the control keeps
        // meaning what people expect. Named constants rather than inline literals,
        // because inline literals are exactly what went missing here.
        MaxButton.Content = WindowState == WindowState.Maximized
            ? GlyphRestore
            : GlyphMaximise;

    private bool _themeSwitching;

    /// <summary>
    /// Cross-fades the chrome through the theme swap. Replacing the token
    /// dictionary repaints every brush in one frame, which is a hard flash on a
    /// window this dark or this light.
    ///
    /// Only the CHROME is faded, never the whole window. The page cannot fade
    /// with it -- WebView2 is a child HWND and ignores WPF opacity entirely
    /// (k.wpf-airspace) -- so dipping the window would produce a broken-looking
    /// transition where the frame dissolves and the content sits there. Fading
    /// only what actually changes colour is also the honest description of what
    /// a theme switch does: the page is not themed by us.
    /// </summary>
    private async void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (_themeSwitching) return;
        _themeSwitching = true;

        try
        {
            var chrome = new UIElement[] { TitleBar, NavBar, StatusBar };

            await Task.WhenAll(chrome.Select(
                c => Motion.FadeAsync(c, 0, Motion.Quick, Motion.Exit)));

            var next = ThemeManager.Cycle();
            ThemeButton.ToolTip = $"Theme: {next}";

            // Let the new brushes be picked up before anything is visible again.
            UpdateLayout();

            await Task.WhenAll(chrome.Select(
                c => Motion.FadeAsync(c, 1, Motion.Base, Motion.Enter)));
        }
        finally
        {
            _themeSwitching = false;
        }
    }

    // Sync -------------------------------------------------------------------

    private void Sync()
    {
        if (_tabs is null) return;

        var active = _tabs.Active;

        // Don't clobber a URL someone is halfway through typing -- but DO
        // overwrite when the tab itself changed underneath them. Guarding on
        // focus alone left the address bar showing the previous tab's URL after
        // a switch, which is the one thing an address bar must never do: it is
        // the only place the browser states what you are looking at.
        var switched = !ReferenceEquals(active, _addressBarTab);
        if (active is not null && (switched || !AddressBar.IsFocused))
        {
            AddressBar.Text = active.Url;
            _addressBarTab = active;
        }

        _syncing = true;
        if (!ReferenceEquals(TabStrip.SelectedItem, active)) TabStrip.SelectedItem = active;
        _syncing = false;

        // A title arriving changes a chip's width, so the marker has to follow
        // it. Queued rather than called: the chip has not been re-measured yet
        // at the moment the title lands, and Sync runs far more often than the
        // strip actually changes.
        QueueTabIndicator();

        var core = active?.View?.CoreWebView2;
        BackButton.IsEnabled = core?.CanGoBack ?? false;
        ForwardButton.IsEnabled = core?.CanGoForward ?? false;
        ReloadButton.IsEnabled = core is not null;

        SyncShield(active);
        SyncTitle(active);

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

    /// <summary>
    /// "Page title - Hearth", the way every browser does it.
    ///
    /// The title is what Alt+Tab and the taskbar preview show, so it is one of
    /// the few places the app has to state its own name. It previously read
    /// "Hearth - WebView2 150.0.4078.83", which is a developer's debug string
    /// wearing the product's clothes and made the app look like a test harness
    /// rather than a browser.
    /// </summary>
    private void SyncTitle(BrowserTab? active)
    {
        const string AppName = "Hearth";

        var page = active is null || string.IsNullOrWhiteSpace(active.DisplayLabel)
            ? null
            : active.DisplayLabel;

        var name = Immersive ? $"{AppName} {Dash} immersion" : AppName;
        Title = page is null ? name : $"{page} {Dash} {name}";
    }

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
            Diag.Log($"placeholder: no snapshot for {tab.HostLabel}");
            HidePlaceholder();
            return;
        }

        Diag.Log($"placeholder: showing {tab.HostLabel}");

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
        Placeholder.BeginAnimation(OpacityProperty, null);
        Placeholder.Opacity = 1;
        Placeholder.Visibility = Visibility.Collapsed;
        Placeholder.Source = null;
        StopLoading();
    }

    /// <summary>
    /// Holds the snapshot on screen until the rebuilt tab has actually painted.
    /// Returning is what lets the live control be shown.
    ///
    /// The wait replaces a flat 220 ms guess from commit 0004, and a guess is
    /// wrong in both directions: too short for a cold tab doing a real network
    /// load, needlessly long for one that was already warm. NavigationCompleted
    /// sets HasRendered, which is the real signal.
    ///
    /// NOT A CROSS-FADE, and it never was. Through 0014 this method ended by
    /// dissolving the placeholder over Motion.Slow, which reads well in source
    /// and did nothing at all on screen: by that point the live WebView2 was
    /// already up, and a child HWND covers WPF content whatever its opacity
    /// (k.wpf-airspace). The honest transition available here is to hold the
    /// picture until the real thing is ready and then cut -- which is invisible,
    /// because both frames are the same page at the same scroll offset. It is
    /// the same handoff the tab grid uses when a card becomes a page.
    /// </summary>
    private async Task HoldPlaceholderUntilPaintedAsync(BrowserTab tab)
    {
        StartLoading();

        // WHICH signal to wait for depends on whether there is a picture up.
        //
        // With a snapshot on screen, the wait is for the live page to MATCH it,
        // so the cut between the two is invisible: that is NavigationCompleted,
        // the strict signal. With no snapshot the user is looking at the app's
        // empty ground, and there is nothing to match -- the page should arrive
        // the moment it exists, which is DOMContentLoaded
        // (k.navigation-completed-is-not-first-paint).
        var holdingPicture = Placeholder.Visibility == Visibility.Visible;

        // Cap the wait. A page that never finishes loading must not leave a
        // screenshot pinned over it forever.
        for (var waited = 0; waited < 100; waited++)
        {
            if (holdingPicture ? tab.HasRendered : tab.HasContent) break;
            await Task.Delay(50);
        }

        // One more beat so the first painted frame is actually up before the
        // control is shown. Longer without a picture, because DOMContentLoaded
        // means the document is PARSED, not drawn: revealing on the instant it
        // fires showed the renderer's blank white page for a frame or two
        // before the site appeared, and a white flash on a dark browser is the
        // most visible failure available.
        await Task.Delay(holdingPicture ? 90 : 260);

        Diag.Log($"reveal {tab.HostLabel}: picture={holdingPicture} "
               + $"content={tab.HasContent} painted={tab.HasRendered}");
    }

    private void StartLoading()
    {
        LoadingBar.Visibility = Visibility.Visible;

        var travel = Math.Max(ContentArea.ActualWidth, 400);
        LoadingShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(
            -240, travel, TimeSpan.FromMilliseconds(1100))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
    }

    private void StopLoading()
    {
        LoadingShift.BeginAnimation(TranslateTransform.XProperty, null);
        LoadingBar.Visibility = Visibility.Collapsed;
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
        if (sender is Button { Tag: BrowserTab tab }) _ = CloseTabAsync(tab);
        e.Handled = true;
    }

    private void TabStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateTabIndicator();

        if (_syncing || _tabs is null) return;
        if (TabStrip.SelectedItem is BrowserTab tab && !ReferenceEquals(tab, _tabs.Active))
            _ = _tabs.ActivateAsync(tab);
    }

    // ------------------------------------------------------------------------
    // The tab strip
    // ------------------------------------------------------------------------

    /// <summary>
    /// Moves the one selection marker to the selected chip.
    ///
    /// Position is a transform and therefore free; WIDTH is a layout property
    /// and is animated anyway, which is a deliberate exception to
    /// d.animate-transforms-only rather than an oversight. The alternative,
    /// scaling a fixed-width border on X, distorts its rounded corners into
    /// ellipses over the length of the travel. The exception is safe only
    /// because the marker lives in a Canvas: a Canvas reports no size of its
    /// own and gives children infinite space, so re-measuring this border cannot
    /// change anything above it. The invalidation stops at the Canvas instead of
    /// walking out into the tab strip and the window.
    /// </summary>
    private void UpdateTabIndicator(bool animate = true)
    {
        if (TabStrip.SelectedItem is null)
        {
            _indicatorPlaced = false;
            TabIndicator.BeginAnimation(OpacityProperty, Motion.To(0, Motion.Quick, Motion.Exit));
            return;
        }

        if (TabStrip.ItemContainerGenerator.ContainerFromItem(TabStrip.SelectedItem)
            is not ListBoxItem chip || chip.ActualWidth <= 0)
        {
            // The container has not been generated or laid out yet -- opening
            // the first tab always lands here. Try again once layout has run,
            // but ONLY ONCE.
            //
            // Retrying unconditionally is an endless self-scheduling loop, and
            // it does not stay theoretical: in immersion the tab strip is lifted
            // into an EdgeBar window that is not shown until the pointer reaches
            // the top of the screen, so its chips have no size and never will.
            // The queue is at Loaded priority, which runs inside the layout
            // phase, so the loop kept that phase permanently busy -- and the
            // symptom was nothing to do with the tab strip at all: the page went
            // blank, because HwndHost never got a settled layout pass in which
            // to show the window it hosts (k.starving-the-layout-phase-blanks-webview2).
            if (!_indicatorRetried)
            {
                _indicatorRetried = true;
                QueueTabIndicator();
            }
            return;
        }

        _indicatorRetried = false;

        var origin = chip.TransformToVisual(TabIndicatorLayer).Transform(new Point(0, 0));

        // Sync runs on every state change a tab reports, and a busy page reports
        // dozens: each refused subresource bumps the blocked count. Restarting
        // the travel animation on every one of those would leave the marker
        // permanently easing towards a position it is already at, which looks
        // like a stutter and is one.
        if (_indicatorPlaced
            && Math.Abs(origin.X - _indicatorX) < 0.5
            && Math.Abs(chip.ActualWidth - _indicatorWidth) < 0.5) return;

        _indicatorX = origin.X;
        _indicatorWidth = chip.ActualWidth;

        Canvas.SetTop(TabIndicator, origin.Y);
        TabIndicator.Height = chip.ActualHeight;

        // The first placement is a jump, not a slide. There is nowhere to travel
        // from, and animating in from x=0 would send the marker skating across
        // the strip every time the browser starts.
        if (!_indicatorPlaced || !animate)
        {
            TabIndicator.BeginAnimation(WidthProperty, null);
            TabIndicatorShift.BeginAnimation(TranslateTransform.XProperty, null);

            TabIndicator.Width = chip.ActualWidth;
            TabIndicatorShift.X = origin.X;

            if (!_indicatorPlaced)
                TabIndicator.BeginAnimation(OpacityProperty, Motion.From(0, 1, Motion.Base));

            _indicatorPlaced = true;
            return;
        }

        // Symmetric easing, because this is travel between two places: the
        // fastest part is the middle, which is how a real object crosses a gap.
        TabIndicator.BeginAnimation(WidthProperty,
            Motion.To(chip.ActualWidth, Motion.Base, Motion.Travel));
        TabIndicatorShift.BeginAnimation(TranslateTransform.XProperty,
            Motion.To(origin.X, Motion.Base, Motion.Travel));
    }

    /// <summary>
    /// Coalesces indicator updates onto one deferred pass. Called from several
    /// places that each fire more often than the strip actually moves.
    /// </summary>
    private void QueueTabIndicator()
    {
        if (_indicatorQueued) return;
        _indicatorQueued = true;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _indicatorQueued = false;
            UpdateTabIndicator();
        });
    }

    private void OnTabsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        QueueTabIndicator();

        if (e.Action is not System.Collections.Specialized.NotifyCollectionChangedAction.Add) return;
        if (e.NewItems is null || !Motion.Enabled) return;

        foreach (BrowserTab added in e.NewItems)
        {
            // The container does not exist until the ItemsControl has generated
            // and laid it out, so the entrance is queued rather than started.
            var tab = added;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => AnimateChipIn(tab));
        }
    }

    /// <summary>
    /// A new chip slides in from the left edge of its own slot.
    ///
    /// Not a width expansion, which is what Chrome does: growing a chip from
    /// zero re-measures the whole strip on every frame, and the strip is inside
    /// the window's caption. A short travel plus a fade buys the same reading --
    /// something arrived here -- for a transform and an opacity.
    /// </summary>
    private void AnimateChipIn(BrowserTab tab)
    {
        if (TabStrip.ItemContainerGenerator.ContainerFromItem(tab) is not ListBoxItem chip)
            return;

        var slide = new TranslateTransform(-16, 0);
        chip.RenderTransform = slide;

        chip.BeginAnimation(OpacityProperty, Motion.From(0, 1, Motion.Base));
        slide.BeginAnimation(TranslateTransform.XProperty,
            Motion.From(-16, 0, Motion.Slow, Motion.Emphasis));
    }

    /// <summary>
    /// Plays the chip out before the tab is actually closed.
    ///
    /// The exit lives in the shell rather than in TabManager, and the ordering
    /// is the reason: the manager removes a tab synchronously, and it should
    /// keep doing that. Making the model wait for an animation would put a
    /// presentation concern inside the class that owns renderer lifetime, which
    /// is the last place it belongs.
    /// </summary>
    private async Task CloseTabAsync(BrowserTab tab)
    {
        if (_tabs is null || !_closingTabs.Add(tab)) return;

        try
        {
            if (Motion.Enabled
                && TabStrip.ItemContainerGenerator.ContainerFromItem(tab) is ListBoxItem chip)
            {
                var slide = new TranslateTransform();
                chip.RenderTransform = slide;

                slide.BeginAnimation(TranslateTransform.XProperty,
                    Motion.To(-14, Motion.Quick, Motion.Exit));
                await Motion.FadeAsync(chip, 0, Motion.Quick, Motion.Exit);
            }

            _tabs.Close(tab);
        }
        finally
        {
            _closingTabs.Remove(tab);
        }
    }

    /// <summary>
    /// Sizes the immersion chip's two states from the real text metrics, so the
    /// hover expansion lands exactly on the label rather than on a number
    /// somebody typed once and that a font change would falsify.
    ///
    /// The slot is fixed at the EXPANDED size and the button is right-aligned
    /// inside it, so expansion consumes space that was already reserved. Nothing
    /// outside the slot re-measures and no neighbouring control moves.
    /// </summary>
    private void MeasureImmersionChip()
    {
        var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);

        ImmersionLabel.Measure(unbounded);
        _immersionLabelWidth = ImmersionLabel.DesiredSize.Width;

        // The clip's child is a Canvas, which reports no height of its own, so
        // the clip has to be told how tall the text it is hiding actually is.
        ImmersionLabelClip.Height = ImmersionLabel.DesiredSize.Height;
        ImmersionLabelClip.Width = 0;

        ImmersionButton.Measure(unbounded);
        ImmersionSlot.Width = ImmersionButton.DesiredSize.Width + _immersionLabelWidth;

        ImmersionButton.MouseEnter += (_, _) => ExpandImmersionChip(true);
        ImmersionButton.MouseLeave += (_, _) => ExpandImmersionChip(false);

        // Keyboard focus opens it too. A control whose meaning is only available
        // to a pointer is one a keyboard user has to guess at.
        ImmersionButton.GotKeyboardFocus += (_, _) => ExpandImmersionChip(true);
        ImmersionButton.LostKeyboardFocus += (_, _) => ExpandImmersionChip(false);
    }

    private void ExpandImmersionChip(bool open) =>
        ImmersionLabelClip.BeginAnimation(WidthProperty, Motion.To(
            open ? _immersionLabelWidth : 0,
            open ? Motion.Base : Motion.Quick,
            open ? Motion.Emphasis : Motion.Exit));

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

        if (Immersive)
        {
            // The chrome is not in this window's layout at all -- it lives in the
            // EdgeBars. Collapsing it here would empty those instead of hiding
            // anything, so the grid simply takes the edges off duty.
            _edgesEnabled = false;
            ConcealEdges();
        }
        else
        {
            // The tab strip doubles as the title bar, so in browse it stays:
            // losing it would take the window controls with it.
            NavBar.Visibility = Visibility.Collapsed;
            StatusBar.Visibility = Visibility.Collapsed;
        }

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

        if (FrameMeter.Start("grid-enter") is { } meter)
            _ = Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(Motion.Slow.TimeSpan);
                meter.Dispose();
            });

        Sync();
    }

    /// <param name="activate">
    /// The tab to switch to, if the grid is being left by picking one.
    ///
    /// It is activated HERE, after the shell is restored, and that ordering is
    /// load-bearing rather than tidy - see <see cref="RestoreShell"/>.
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

        if (Immersive)
        {
            // Put the edges back on duty, concealed, ready for the next time the
            // pointer reaches for them. Restoring the window state here would
            // drop the window out of fullscreen.
            _edgesEnabled = true;
        }
        else
        {
            TitleBar.Visibility = Visibility.Visible;
            NavBar.Visibility = Visibility.Visible;
            StatusBar.Visibility = Visibility.Visible;
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
    /// <summary>
    /// Brings the grid in or out.
    ///
    /// Fades the SCRIM, HEADER and FOOTER individually rather than the whole
    /// container. Opacity on a large subtree is not free -- WPF has to composite
    /// that subtree into an offscreen surface every frame -- and fading this
    /// container measured about a third of the frame rate away
    /// (k.subtree-opacity-is-not-free). The cards are not included here either;
    /// they carry their own staggered entrance.
    /// </summary>
    private void AnimateBigPicture(bool fadeIn)
    {
        var duration = Motion.Slow;
        var easing = fadeIn ? Motion.Emphasis : Motion.Exit;
        var target = fadeIn ? 1 : 0;

        // The container itself is always fully opaque; only its parts fade.
        BigPicture.Opacity = 1;

        foreach (var part in new UIElement[] { GridScrim, GridHeader, GridFooter })
            part.BeginAnimation(OpacityProperty, Motion.To(target, duration, easing));

        // Scaling from 0.94 rather than 0.965: a bigger travel over a longer
        // beat reads as a surface moving toward you. Transforms need no
        // offscreen surface, so this one is genuinely cheap.
        BigPictureScale.ScaleX = BigPictureScale.ScaleY = fadeIn ? 0.94 : 1;

        var scaleTo = fadeIn ? 1 : 0.94;
        BigPictureScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            Motion.To(scaleTo, duration, easing));
        BigPictureScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            Motion.To(scaleTo, duration, easing));
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

        for (var i = 0; i < TabWall.Items.Count; i++)
        {
            if (TabWall.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem card)
                continue;   // Virtualised away, i.e. off-screen. Nothing to stagger.

            // Rise AND scale, rather than rise alone. Two properties moving
            // together read as one object arriving; a lone translate reads as a
            // list scrolling.
            var lift = new TranslateTransform(0, 22);
            var grow = new ScaleTransform(0.96, 0.96);
            var group = new TransformGroup();
            group.Children.Add(grow);
            group.Children.Add(lift);

            card.RenderTransformOrigin = new Point(0.5, 0.5);
            card.RenderTransform = group;
            card.Opacity = 0;

            var begin = TimeSpan.FromMilliseconds(Math.Min(i, 12) * 34);

            card.BeginAnimation(OpacityProperty,
                Motion.From(0, 1, Motion.Base, Motion.Enter, begin));
            lift.BeginAnimation(TranslateTransform.YProperty,
                Motion.From(22, 0, Motion.Slow, Motion.Emphasis, begin));
            grow.BeginAnimation(ScaleTransform.ScaleXProperty,
                Motion.From(0.96, 1, Motion.Slow, Motion.Emphasis, begin));
            grow.BeginAnimation(ScaleTransform.ScaleYProperty,
                Motion.From(0.96, 1, Motion.Slow, Motion.Emphasis, begin));
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

            // Hand the zoomed card over to the placeholder BEFORE the shell is
            // restored, so the picture that just flew up to fill the screen is
            // still the picture on screen once the grid goes away. Without this
            // handoff there is a hole between the animation ending and the page
            // painting, which is precisely what made the transition feel clunky.
            ShowPlaceholder(tab);

            // Take the pages off screen before the shell comes back. The grid
            // has to uncollapse the content host before it can activate
            // anything, and whatever was live is still sitting in it -- filmed,
            // that showed as a frame of the page being LEFT appearing between
            // the card finishing its zoom and the new page arriving.
            _tabs.Blank();

            await CrossFadeGridOutAsync();

            // The hold now happens inside ActivateAsync, through the reveal
            // gate, so a card that needs rebuilding keeps its picture up until
            // the page is ready. A card that was already live has nothing to
            // wait for and the live control is already covering this, so all
            // that is left is to put the placeholder away.
            await LeaveGridAsync(zoomed: true, activate: tab);
            HidePlaceholder();

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
        // The one deliberately large movement in the app, so it gets the longest
        // duration and a symmetric curve: fastest in the middle, soft at both
        // ends, the way a real object travels.
        var duration = Motion.Grand;

        var card = TabWall.ItemContainerGenerator.ContainerFromItem(tab) as ListBoxItem;
        var viewportWidth = BigPicture.ActualWidth;
        var viewportHeight = BigPicture.ActualHeight;

        // Virtualised away, or laid out at zero size. Falling back to the plain
        // fade is correct here: a broken animation must never block the open.
        if (card is null || card.ActualWidth <= 0 || viewportWidth <= 0)
        {
            AnimateBigPicture(fadeIn: false);
            await Task.Delay(Motion.Slow.TimeSpan);
            return;
        }

        var origin = card.TransformToVisual(BigPicture).Transform(new Point(0, 0));

        // A LIVE VisualBrush, not a cached bitmap, and that is a measured choice
        // rather than the obvious one (k.cached-bitmap-loses-to-visualbrush).
        //
        // Caching the card into a frozen RenderTargetBitmap and flying that looks
        // like the clear optimisation -- rasterise once instead of re-rendering
        // per frame -- so 0013 built it. Measured on one build with only this
        // varying, it cost THREE TIMES the CPU: 47 ms across the animation
        // against 16 ms for the VisualBrush.
        //
        // The rasterisation is not the expense; it finishes before the
        // measurement window opens. The expense is resampling. This card is
        // 308px wide and grows to fill the viewport, so a bitmap gets upscaled
        // roughly four times on every frame. The VisualBrush instead re-renders
        // the card's vectors and text AT the size being drawn, which is both
        // cheaper and sharper -- an upscaled bitmap goes soft exactly when the
        // card is largest and most looked at.
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

        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            Motion.To(target, duration, Motion.Travel));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            Motion.To(target, duration, Motion.Travel));

        slide.BeginAnimation(TranslateTransform.XProperty, Motion.To(
            viewportWidth / 2 - (origin.X + card.ActualWidth / 2), duration, Motion.Travel));
        slide.BeginAnimation(TranslateTransform.YProperty, Motion.To(
            viewportHeight / 2 - (origin.Y + card.ActualHeight / 2), duration, Motion.Travel));

        // The rest of the wall drops away underneath the card that is growing.
        // Faster than the zoom, so the chosen card is unambiguous early.
        TabWall.BeginAnimation(OpacityProperty, Motion.From(1, 0, Motion.Base, Motion.Exit));

        using (FrameMeter.Start("zoom"))
        {
            await Task.Delay(duration.TimeSpan);
        }

        // Deliberately ends HOLDING the card at full size rather than fading it.
        // The caller now puts the same page's snapshot into the placeholder
        // underneath and cross-fades to that, so there is never a frame with
        // nothing on it. Fading here would reintroduce exactly the gap the
        // handoff exists to close.
        card.Opacity = 1;
        TabWall.BeginAnimation(OpacityProperty, null);
        TabWall.Opacity = 1;
    }

    /// <summary>
    /// Dissolves the grid to reveal whatever is behind it -- by this point, the
    /// placeholder showing the same page. The ghost and the snapshot are framed
    /// slightly differently (one is a card, one is the raw capture), so a short
    /// cross-fade covers the difference where a cut would show it.
    /// </summary>
    /// <summary>
    /// Dissolves what is left of the grid to reveal the placeholder behind it.
    /// The cards are already gone by this point -- the zoom faded the wall out
    /// and flew the chosen one -- so this only has the scrim and the two labels
    /// to deal with, and fades them as leaves rather than fading the container
    /// (k.subtree-opacity-is-not-free).
    /// </summary>
    private Task CrossFadeGridOutAsync() =>
        Task.WhenAll(
            Motion.FadeAsync(GridScrim, 0, Motion.Base, Motion.Enter),
            Motion.FadeAsync(GridHeader, 0, Motion.Base, Motion.Enter),
            Motion.FadeAsync(GridFooter, 0, Motion.Base, Motion.Enter));

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
