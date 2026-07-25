using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;

namespace Hearth.Views;

public partial class MainWindow : Window
{
    private const string HomeUrl = "https://example.com";

    private CoreWebView2Environment? _environment;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // A single explicitly-created environment is the cornerstone of the whole
        // design: every WebView2 created from one environment shares ONE browser
        // process. Letting each control create its own (the default) would spawn a
        // browser process per tab and defeat the memory budget before it exists.
        var userDataFolder = Path.Combine(App.StoreRoot, "webview2");
        Directory.CreateDirectory(userDataFolder);

        _environment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: userDataFolder);

        await Browser.EnsureCoreWebView2Async(_environment);

        Browser.CoreWebView2.DocumentTitleChanged += (_, _) => UpdateStatus();
        Browser.CoreWebView2.SourceChanged += (_, _) =>
        {
            AddressBar.Text = Browser.CoreWebView2.Source;
            UpdateStatus();
        };

        AddressBar.Text = HomeUrl;
        Browser.CoreWebView2.Navigate(HomeUrl);
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_environment is null) return;

        var title = Browser.CoreWebView2?.DocumentTitle;
        StatusText.Text =
            $"Runtime {_environment.BrowserVersionString}  ·  1 live tab  ·  {title}";
    }

    private void Navigate()
    {
        if (Browser.CoreWebView2 is null) return;

        var raw = AddressBar.Text.Trim();
        if (raw.Length == 0) return;

        // Anything without a scheme that also lacks a dot is treated as a search.
        var url = raw.Contains("://")
            ? raw
            : raw.Contains('.') && !raw.Contains(' ')
                ? "https://" + raw
                : "https://duckduckgo.com/?q=" + Uri.EscapeDataString(raw);

        Browser.CoreWebView2.Navigate(url);
    }

    private void GoButton_Click(object sender, RoutedEventArgs e) => Navigate();

    private void AddressBar_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Navigate();
    }
}
