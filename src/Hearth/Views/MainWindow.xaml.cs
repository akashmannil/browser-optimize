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

    private TabManager? _tabs;
    private DispatcherTimer? _memoryTimer;
    private long _peakBytes;
    private WindowState _stateBeforeBigPicture = WindowState.Normal;

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
        StateChanged += (_, _) => UpdateMaximiseGlyph();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _tabs = new TabManager(ContentHost, HearthOptions.FromEnvironment());
        _tabs.Changed += (_, _) => Dispatcher.Invoke(Sync);
        _tabs.Rehydrating += (_, tab) => Dispatcher.Invoke(() => ShowPlaceholder(tab));
        _tabs.Rehydrated += (_, _) => Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(220);
            HidePlaceholder();
        });

        TabStrip.ItemsSource = _tabs.Tabs;
        TabWall.ItemsSource = _tabs.Tabs;

        if (_tabs.Options.StartInLowPower)
        {
            await _tabs.SetLowPowerAsync(true);
            LowPowerToggle.IsChecked = true;
        }

        var startup = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (startup.Length == 0)
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
        Title = $"Hearth {Dash} WebView2 {version}";
        Sync();
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
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";

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

        // "Awake" and "resting", not "live" and "hibernated". Resting carries
        // the promise that matters: it will be there when you come back.
        var resting = _tabs.Tabs.Count - _tabs.LiveCount;
        TabsText.Text = resting > 0
            ? $"{_tabs.LiveCount} awake {Dot} {resting} resting"
            : $"{_tabs.LiveCount} awake";
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

    private void NewTab_Click(object sender, RoutedEventArgs e)
    {
        _tabs?.Open(HomeUrl);
        AddressBar.Focus();
        AddressBar.SelectAll();
    }

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

    private async void LowPower_Changed(object sender, RoutedEventArgs e)
    {
        if (_tabs is null) return;
        await _tabs.SetLowPowerAsync(LowPowerToggle.IsChecked == true);
        Sync();
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
            WindowState = WindowState.Maximized;

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
            TitleBar.Visibility = Visibility.Visible;
            NavBar.Visibility = Visibility.Visible;
            StatusBar.Visibility = Visibility.Visible;
            ContentHost.Visibility = Visibility.Visible;
            WindowState = _stateBeforeBigPicture;

            await _tabs.SetBigPictureAsync(false);

            // Leaving the wall means the user surveyed and chose. Everything
            // else goes back to sleep now rather than waiting for some later
            // activation to push it past the ceiling.
            await _tabs.HibernateAllButActiveAsync();

            AddressBar.Focus();
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

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var showing = BigPicture.Visibility == Visibility.Visible;

        switch (e.Key)
        {
            case Key.F11:
                e.Handled = true;
                await SetBigPictureAsync(!showing);
                break;

            // Escape belongs to the page unless the wall is up.
            case Key.Escape when showing:
                e.Handled = true;
                await SetBigPictureAsync(false);
                break;
        }
    }
}
