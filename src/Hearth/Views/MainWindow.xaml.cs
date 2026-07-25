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
