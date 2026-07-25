using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Hearth.Core;

/// <summary>
/// Owns tab lifetime, the single shared <see cref="CoreWebView2Environment"/>,
/// and enforcement of the live-tab budget.
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
    private readonly HearthOptions _options;
    private readonly Task<CoreWebView2Environment> _environmentTask;

    /// <summary>
    /// Serialises budget enforcement. Activation is async and user-driven, so
    /// two rapid tab switches can otherwise interleave and evict past the budget
    /// or race on the same tab's controller.
    /// </summary>
    private readonly SemaphoreSlim _budgetGate = new(1, 1);

    public TabManager(Panel contentHost, HearthOptions? options = null)
    {
        _contentHost = contentHost;
        _options = options ?? HearthOptions.Default;
        _environmentTask = CreateSharedEnvironmentAsync(_options);
        Tabs = new ReadOnlyObservableCollection<BrowserTab>(_tabs);
    }

    public ReadOnlyObservableCollection<BrowserTab> Tabs { get; }

    /// <summary>Screenshots and scroll state for tabs that hold no renderer.</summary>
    public SnapshotStore Snapshots { get; } = new();

    public BrowserTab? Active { get; private set; }

    public HearthOptions Options => _options;

    /// <summary>Raised when the active tab changes or any tab's state changes.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Raised when activating a tab that holds no renderer, before rebuilding
    /// starts. The shell paints the tab's captured frame here so the rebuild
    /// happens behind an image of the page rather than behind nothing.
    /// </summary>
    public event EventHandler<BrowserTab>? Rehydrating;

    /// <summary>Raised once live content is behind the placeholder.</summary>
    public event EventHandler<BrowserTab>? Rehydrated;

    /// <summary>Tabs currently holding a full renderer. This is what the budget caps.</summary>
    public int LiveCount => _tabs.Count(t => t.State == TabState.Live);

    /// <summary>Whether low-power mode is engaged.</summary>
    public bool LowPower { get; private set; }

    /// <summary>Whether the full-screen tab wall is showing.</summary>
    public bool BigPicture { get; private set; }

    /// <summary>
    /// The budget actually in force. Big Picture pins it to one: you are looking
    /// at a wall of pictures, so exactly one page needs to be real. The lean-back
    /// aesthetic and the memory architecture want the same thing here, which is
    /// the reason this mode is worth having at all rather than being a skin.
    /// </summary>
    public int EffectiveBudget => BigPicture
        ? 1
        : LowPower ? _options.LowPowerBudget : _options.LiveTabBudget;

    /// <summary>
    /// Enters or leaves Big Picture. On entry the active tab is captured first,
    /// so the wall shows what the user was actually last looking at rather than a
    /// stale frame from whenever it was last blurred.
    /// </summary>
    public async Task SetBigPictureAsync(bool on)
    {
        if (BigPicture == on) return;

        if (on && Active is { } current && current.View?.CoreWebView2 is not null)
            await Snapshots.CaptureAsync(current);

        BigPicture = on;

        await _budgetGate.WaitAsync();
        try { await EnforceBudgetAsync(); }
        finally { _budgetGate.Release(); }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Engages or releases low-power mode. Content filtering only affects
    /// requests made from now on, so the active tab is reloaded to give the
    /// change an immediate, visible effect rather than one that arrives
    /// silently three navigations later.
    /// </summary>
    public async Task SetLowPowerAsync(bool on)
    {
        if (LowPower == on) return;
        LowPower = on;

        foreach (var tab in _tabs)
            if (tab.View?.CoreWebView2 is { } core) ApplyContentPolicy(core);

        await _budgetGate.WaitAsync();
        try { await EnforceBudgetAsync(); }
        finally { _budgetGate.Release(); }

        if (Active?.View?.CoreWebView2 is { } activeCore) activeCore.Reload();

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Installs or clears the low-power request filter on a realised tab.
    /// Registering the filter is idempotent, so the handler alone is toggled.
    /// </summary>
    private void ApplyContentPolicy(CoreWebView2 core)
    {
        core.WebResourceRequested -= OnWebResourceRequested;
        if (!LowPower || !_options.BlockThirdPartyFrames) return;

        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Document);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Media);
        core.WebResourceRequested += OnWebResourceRequested;
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (sender is not CoreWebView2 core) return;

        // Media is refused outright in low-power mode: autoplaying video is the
        // single most expensive thing a background page can do.
        if (e.ResourceContext == CoreWebView2WebResourceContext.Media)
        {
            e.Response = core.Environment.CreateWebResourceResponse(null, 204, "Blocked", "");
            return;
        }

        if (e.ResourceContext != CoreWebView2WebResourceContext.Document) return;

        // A Document request whose host differs from the top-level page is a
        // cross-origin subframe — an ad, a widget, an embedded player. Each one
        // costs a dedicated renderer under site isolation.
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var requested)) return;
        if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var top)) return;
        if (string.Equals(requested.Host, top.Host, StringComparison.OrdinalIgnoreCase)) return;

        // Never block the top-level navigation itself.
        if (string.Equals(e.Request.Uri, core.Source, StringComparison.OrdinalIgnoreCase)) return;

        e.Response = core.Environment.CreateWebResourceResponse(null, 204, "Blocked", "");
    }

    /// <summary>Tabs suspended but still holding a controller.</summary>
    public int HibernatedCount => _tabs.Count(t => t.State == TabState.Hibernated);

    public async Task<string> GetRuntimeVersionAsync() =>
        (await _environmentTask).BrowserVersionString;

    private static async Task<CoreWebView2Environment> CreateSharedEnvironmentAsync(
        HearthOptions options)
    {
        var folder = Path.Combine(App.StoreRoot, "webview2");
        Directory.CreateDirectory(folder);

        // --renderer-process-limit is the only embedder-side lever on the process
        // model (api.additional-browser-arguments). Without it the live-tab budget
        // is toothless: commit 0002 measured 14 renderers for 5 tabs because site
        // isolation gives every cross-origin iframe its own process, so capping
        // tabs caps almost nothing. See HearthOptions for the security trade-off.
        var environmentOptions = new CoreWebView2EnvironmentOptions();
        var args = options.BrowserArguments();
        if (args.Length > 0)
            environmentOptions.AdditionalBrowserArguments = args;

        return await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: folder,
            options: environmentOptions);
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

        // A tab with no controller has to be rebuilt from scratch, which is the
        // only case the user could otherwise see as a blank pane.
        var needsRebuild = tab.View is null && Snapshots.Has(tab.Id);
        if (needsRebuild) Rehydrating?.Invoke(this, tab);

        await _budgetGate.WaitAsync();
        try
        {
            // Capture the OUTGOING tab before anything else touches visibility.
            // CapturePreviewAsync can only photograph a controller that is still
            // visible and painted (k.capture-requires-live), so blur is the last
            // moment a snapshot can be taken — by eviction time the tab has long
            // since been collapsed and there is nothing left to photograph.
            if (Active is { } outgoing
                && !ReferenceEquals(outgoing, tab)
                && outgoing.View?.CoreWebView2 is not null)
            {
                await Snapshots.CaptureAsync(outgoing);
            }

            await RealiseAsync(tab);
            if (needsRebuild) Rehydrated?.Invoke(this, tab);

            Active = tab;
            tab.LastActivatedUtc = DateTime.UtcNow;
            tab.ActivationCount++;

            foreach (var other in _tabs)
            {
                if (other.View is not null)
                    other.View.Visibility = ReferenceEquals(other, tab)
                        ? Visibility.Visible
                        : Visibility.Collapsed;
            }

            await EnforceBudgetAsync();
        }
        finally
        {
            _budgetGate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Evicts lowest-scoring live tabs until the budget is satisfied. Called
    /// after every activation, so the ceiling holds by construction rather than
    /// reactively under memory pressure — which is the difference between this
    /// and Chrome Memory Saver (c.hibernate-by-default).
    /// </summary>
    private async Task EnforceBudgetAsync()
    {
        var now = DateTime.UtcNow;
        var overBudget = LiveCount - EffectiveBudget;
        if (overBudget <= 0) return;

        foreach (var victim in EvictionPolicy.EvictionOrder(_tabs, Active, now).Take(overBudget))
            await HibernateAsync(victim);
    }

    /// <summary>
    /// Hibernation tier: suspends the renderer while keeping the controller, so
    /// resume is roughly 100 ms rather than a full page load. Requires the
    /// controller to be invisible first, which the WPF wrapper does via
    /// Visibility.
    /// </summary>
    private async Task HibernateAsync(BrowserTab tab)
    {
        if (tab.View?.CoreWebView2 is not { } core) return;

        tab.View.Visibility = Visibility.Collapsed;

        // No capture here — by this point the tab was collapsed at blur and
        // cannot be photographed. We rely on the snapshot taken when the user
        // switched away from it, which is the only moment one was available.
        //
        // Teardown is permitted only when such a snapshot exists: evicting a tab
        // we cannot repaint is the exact papercut that makes users disable every
        // other suspender (p.lossy-restore). Without one we fall back to
        // suspension, which at least keeps the page there to look at.
        if (_options.AllowFullTeardown && Snapshots.Has(tab.Id))
        {
            Destroy(tab);
            return;
        }

        try
        {
            // TrySuspend legitimately returns false — media playback, downloads
            // and active connections all block suspension. A refusal is a normal
            // outcome, not an error: the tab simply stays Live and remains a
            // candidate at the next activation.
            var suspended = await core.TrySuspendAsync();
            tab.State = suspended ? TabState.Hibernated : TabState.Live;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[hearth] suspend failed for {tab.Url}: {ex.Message}");
            tab.State = TabState.Live;
        }
    }

    /// <summary>
    /// Brings a tab from Cold to Live, creating its renderer, or resumes it from
    /// Hibernated. The only place in the codebase permitted to call
    /// EnsureCoreWebView2Async.
    /// </summary>
    private async Task RealiseAsync(BrowserTab tab)
    {
        if (tab.View is not null)
        {
            if (tab.State == TabState.Hibernated)
                tab.View.CoreWebView2?.Resume();

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
        ApplyContentPolicy(core);

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

        // Returning a rehydrated tab to the top of the page is the most-noticed
        // restore failure in every other suspender, so replay the captured
        // offset as soon as the document is ready.
        core.NavigationCompleted += async (_, e) =>
        {
            if (e.IsSuccess)
            {
                tab.HasRendered = true;
                await Snapshots.RestoreScrollAsync(tab);
            }
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
        Snapshots.Forget(tab.Id);

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

        // A rebuilt tab has not painted again yet, so it is not photographable
        // until its next navigation completes.
        tab.HasRendered = false;
    }
}
