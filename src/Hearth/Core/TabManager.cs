using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Hearth.Core;

/// <summary>
/// Owns tab lifetime and — critically — the single shared
/// <see cref="CoreWebView2Environment"/> every tab is realised against.
///
/// INVARIANT (d.shared-environment): no code anywhere may call
/// EnsureCoreWebView2Async without the environment this class hands out. One
/// environment means one browser process hosting N renderers. Letting a control
/// self-initialise gives it its OWN browser process, silently, with no error —
/// turning a 20-tab session into 20 browser processes and destroying the memory
/// budget the project exists to enforce. Realisation is funnelled through
/// <see cref="RealiseAsync"/> for exactly this reason.
/// </summary>
public sealed class TabManager
{
    private readonly ObservableCollection<BrowserTab> _tabs = [];
    private readonly Panel _contentHost;

    /// <summary>
    /// Created once, awaited many times. Commit 0001 noted that an explicit
    /// environment makes startup asynchronous; holding the Task (rather than
    /// the result) lets every tab await the same initialisation instead of
    /// racing to create its own.
    /// </summary>
    private readonly Task<CoreWebView2Environment> _environmentTask;

    public TabManager(Panel contentHost)
    {
        _contentHost = contentHost;
        _environmentTask = CreateSharedEnvironmentAsync();
        Tabs = new ReadOnlyObservableCollection<BrowserTab>(_tabs);
    }

    public ReadOnlyObservableCollection<BrowserTab> Tabs { get; }

    public BrowserTab? Active { get; private set; }

    /// <summary>Raised when the active tab changes or any tab's state changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Number of tabs currently holding a full renderer.</summary>
    public int LiveCount => _tabs.Count(t => t.State == TabState.Live);

    public async Task<string> GetRuntimeVersionAsync() =>
        (await _environmentTask).BrowserVersionString;

    private static async Task<CoreWebView2Environment> CreateSharedEnvironmentAsync()
    {
        var folder = Path.Combine(App.StoreRoot, "webview2");
        Directory.CreateDirectory(folder);
        return await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: folder);
    }

    /// <summary>
    /// Adds a tab. Note it starts <see cref="TabState.Cold"/> and holds no
    /// renderer — a tab only costs memory once it is actually activated. This
    /// is the default-state inversion in its smallest form: opening 200 tabs
    /// costs 200 URLs, not 200 renderers.
    /// </summary>
    public BrowserTab Open(string url, bool activate = true)
    {
        var tab = new BrowserTab(url);
        _tabs.Add(tab);

        if (activate) _ = ActivateAsync(tab);
        else Changed?.Invoke(this, EventArgs.Empty);

        return tab;
    }

    public async Task ActivateAsync(BrowserTab tab)
    {
        if (!_tabs.Contains(tab)) return;

        await RealiseAsync(tab);

        foreach (var other in _tabs)
        {
            if (other.View is not null)
                other.View.Visibility = ReferenceEquals(other, tab)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        Active = tab;
        tab.LastActivatedUtc = DateTime.UtcNow;
        tab.ActivationCount++;

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Brings a tab from Cold to Live, creating its renderer. The only place in
    /// the codebase permitted to call EnsureCoreWebView2Async.
    /// </summary>
    private async Task RealiseAsync(BrowserTab tab)
    {
        if (tab.View is not null)
        {
            tab.State = TabState.Live;
            return;
        }

        var view = new WebView2 { Visibility = Visibility.Collapsed };
        tab.View = view;

        // Must be in the visual tree before initialisation: the WPF WebView2
        // needs a realised HWND to host the renderer.
        _contentHost.Children.Add(view);

        var environment = await _environmentTask;
        await view.EnsureCoreWebView2Async(environment);

        var core = view.CoreWebView2;
        core.DocumentTitleChanged += (_, _) =>
        {
            tab.Title = string.IsNullOrWhiteSpace(core.DocumentTitle)
                ? tab.Url
                : core.DocumentTitle;
            Changed?.Invoke(this, EventArgs.Empty);
        };
        core.SourceChanged += (_, _) =>
        {
            tab.Url = core.Source;
            Changed?.Invoke(this, EventArgs.Empty);
        };

        core.Navigate(tab.Url);
        tab.State = TabState.Live;
    }

    public void Navigate(BrowserTab tab, string url)
    {
        tab.Url = url;
        tab.View?.CoreWebView2?.Navigate(url);
    }

    public void Close(BrowserTab tab)
    {
        if (!_tabs.Remove(tab)) return;

        Destroy(tab);

        if (ReferenceEquals(Active, tab))
        {
            Active = null;
            var next = _tabs.LastOrDefault();
            if (next is not null) _ = ActivateAsync(next);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Releases a tab's renderer and returns it to <see cref="TabState.Cold"/>.
    /// Disposing the control is what actually hands memory back to the OS;
    /// removing it from the visual tree alone does not.
    /// </summary>
    private void Destroy(BrowserTab tab)
    {
        if (tab.View is null) return;

        _contentHost.Children.Remove(tab.View);
        tab.View.Dispose();
        tab.View = null;
        tab.State = TabState.Cold;
    }
}
