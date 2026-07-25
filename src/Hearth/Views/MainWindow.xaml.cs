using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hearth.Core;

namespace Hearth.Views;

public partial class MainWindow : Window
{
    private const string HomeUrl = "https://example.com";

    private TabManager? _tabs;

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

        // Live count is the number the budget in commit 0003 will cap. Showing it
        // now, before it is enforced, gives a baseline to compare against.
        var o = _tabs.Options;
        var cap = o.RendererProcessLimit is { } l ? $"{l}" : "∞";

        StatusText.Text =
            $"{_tabs.Tabs.Count} tabs  ·  {_tabs.LiveCount}/{o.LiveTabBudget} live  ·  " +
            $"{_tabs.HibernatedCount} hibernated  ·  renderer cap {cap}  ·  " +
            $"{active?.Title ?? "—"}";
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
