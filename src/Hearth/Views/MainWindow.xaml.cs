using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hearth.Core;

namespace Hearth.Views;

public partial class MainWindow : Window
{
    private const string HomeUrl = "https://example.com";

    private TabManager? _tabs;
    private DispatcherTimer? _memoryTimer;
    private long _peakBytes;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _tabs = new TabManager(ContentHost, HearthOptions.FromEnvironment());
        _tabs.Changed += (_, _) => Dispatcher.Invoke(Sync);
        _tabs.Rehydrating += (_, tab) => Dispatcher.Invoke(() => ShowPlaceholder(tab));

        // The placeholder is held for a beat after the renderer returns: the
        // controller reports ready before it has painted, and dropping the
        // image on that signal shows a white flash instead of hiding one.
        _tabs.Rehydrated += (_, _) => Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(220);
            HidePlaceholder();
        });

        TabStrip.ItemsSource = _tabs.Tabs;

        if (_tabs.Options.StartInLowPower)
        {
            await _tabs.SetLowPowerAsync(true);
            LowPowerToggle.IsChecked = true;
        }

        // Startup URLs may be passed on the command line, which is ordinary
        // browser behaviour and — more usefully here — makes memory runs at a
        // given tab count reproducible without hand-clicking the strip.
        var startup = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (startup.Length == 0)
        {
            _tabs.Open(HomeUrl);
        }
        else if (Environment.GetEnvironmentVariable("HEARTH_ACTIVATE_ALL") is "1" or "true")
        {
            // Benchmark mode: visit every tab in turn so they all become Live and
            // the budget actually binds. Without this the budget can't be
            // measured at all — ordinary startup realises only one tab, so live
            // count never reaches the ceiling.
            foreach (var url in startup)
            {
                var tab = _tabs.Open(url, activate: false);
                await _tabs.ActivateAsync(tab);

                // Dwell until the page has actually painted. ActivateAsync
                // returns once the renderer exists, not once the document has
                // loaded, so advancing immediately would blur every tab before
                // its first paint — producing no snapshots and, in turn, no
                // teardowns. A person reads a page before moving on; a harness
                // that doesn't is measuring a workload nobody has.
                for (var waited = 0; waited < 80 && !tab.HasRendered; waited++)
                    await Task.Delay(100);
            }
        }
        else
        {
            // Only the last becomes active; the rest stay Cold until visited,
            // which is the default-state inversion doing its job.
            for (var i = 0; i < startup.Length; i++)
                _tabs.Open(startup[i], activate: i == startup.Length - 1);
        }

        StartMemorySampling();

        // Surfacing the runtime version proves the shared environment resolved.
        var version = await _tabs.GetRuntimeVersionAsync();
        Title = $"Hearth — WebView2 {version}";
        Sync();
    }

    /// <summary>Pulls window chrome back in line with tab-manager state.</summary>
    private void Sync()
    {
        if (_tabs is null) return;

        var active = _tabs.Active;
        if (active is not null && !AddressBar.IsFocused)
            AddressBar.Text = active.Url;

        var core = active?.View?.CoreWebView2;
        BackButton.IsEnabled = core?.CanGoBack ?? false;
        ForwardButton.IsEnabled = core?.CanGoForward ?? false;
        ReloadButton.IsEnabled = core is not null;

        // "Awake" and "resting" rather than "live" and "hibernated". Resting
        // carries the promise that matters — it will be there when you come
        // back — where the mechanism's own vocabulary carries nothing.
        var resting = _tabs.Tabs.Count - _tabs.LiveCount;
        TabsText.Text = resting > 0
            ? $"{_tabs.LiveCount} awake · {resting} resting"
            : $"{_tabs.LiveCount} awake";
    }

    /// <summary>
    /// Samples real memory on a timer. Measured from our own process tree, never
    /// estimated — the project's whole argument is that the cost is real, and a
    /// fabricated number would forfeit that.
    /// </summary>
    private void StartMemorySampling()
    {
        _memoryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _memoryTimer.Tick += (_, _) =>
        {
            var reading = MemoryProbe.Sample();
            _peakBytes = Math.Max(_peakBytes, reading.TotalBytes);

            MemoryText.Text = MemoryProbe.Format(reading.TotalBytes);

            // Peak-versus-current is the closest honest statement we can make
            // about savings without a counterfactual run: it is what this
            // session actually reached, against what it holds now.
            var saved = _peakBytes - reading.TotalBytes;
            SavedText.Text = saved > 32 * 1024 * 1024
                ? $"{MemoryProbe.Format(saved)} returned since this session's peak"
                : $"{reading.ProcessCount} processes";
        };
        _memoryTimer.Start();
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

    private async void LowPower_Changed(object sender, RoutedEventArgs e)
    {
        if (_tabs is null) return;
        await _tabs.SetLowPowerAsync(LowPowerToggle.IsChecked == true);
        Sync();
    }

    // ── Big Picture ──────────────────────────────────────────────────────────

    private void BigPicture_Click(object sender, RoutedEventArgs e) =>
        _ = SetBigPictureAsync(BigPicture.Visibility != Visibility.Visible);

    private async Task SetBigPictureAsync(bool on)
    {
        if (_tabs is null) return;

        await _tabs.SetBigPictureAsync(on);

        BigPicture.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        // WPF airspace: WebView2 is a windowed HWND control hosted through
        // HwndHost, so it paints above ALL WPF content regardless of Z-order.
        // An overlay cannot be stacked on top of it — the web content has to be
        // taken off screen instead. This is also the honest thing to do here:
        // Big Picture shows pictures, so no live page needs to be visible.
        ContentHost.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        if (on) HidePlaceholder();

        if (on)
        {
            TabWall.ItemsSource = _tabs.Tabs;
            TabWall.SelectedItem = _tabs.Active;
            BigPictureSubtitle.Text =
                $"{_tabs.Tabs.Count} tabs · one stays awake while you're here";

            // Focus the wall so arrow keys work without a click first — the
            // whole point of this mode is that it is driven from a distance.
            TabWall.Focus();
            if (TabWall.SelectedItem is not null)
                TabWall.ScrollIntoView(TabWall.SelectedItem);
        }
        else
        {
            AddressBar.Focus();
        }

        Sync();
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

            // Escape only means "go back" while the wall is up; elsewhere it
            // belongs to the page.
            case Key.Escape when showing:
                e.Handled = true;
                await SetBigPictureAsync(false);
                break;
        }
    }


    private void Navigate()
    {
        if (_tabs?.Active is not { } active) return;

        var raw = AddressBar.Text.Trim();
        if (raw.Length == 0) return;

        // No scheme and no dot means the user typed a query, not an address.
        var url = raw.Contains("://")
            ? raw
            : raw.Contains('.') && !raw.Contains(' ')
                ? "https://" + raw
                : "https://duckduckgo.com/?q=" + Uri.EscapeDataString(raw);

        _tabs.Navigate(active, url);
    }

    /// <summary>
    /// Paints the tab's captured frame while its renderer is rebuilt, then
    /// clears it once live content is behind it. The image is loaded
    /// OnLoad + cached so the file handle is released immediately — otherwise a
    /// later capture to the same path fails with a sharing violation.
    /// </summary>
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

    private void GoButton_Click(object sender, RoutedEventArgs e) => Navigate();

    private void AddressBar_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Navigate();
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

    private void TabStrip_Click(object sender, MouseButtonEventArgs e)
    {
        // The close button handles its own click and marks it handled, so
        // anything reaching here is a request to activate the chip.
        if (e.OriginalSource is not DependencyObject source) return;

        var container = FindAncestorItem(source);
        if (container?.DataContext is BrowserTab tab) _ = _tabs?.ActivateAsync(tab);
    }

    private static FrameworkElement? FindAncestorItem(DependencyObject source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement { DataContext: BrowserTab } element)
                return element;

            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }
}
