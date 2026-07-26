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
/// self-initialise gives it its OWN browser process, silently, with no error --
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
    /// Which tab owns which renderer. WebResourceRequested hands back the
    /// CoreWebView2 and nothing else, and that fires on every subresource of
    /// every page, so resolving the tab by scanning the collection would put a
    /// linear search on the hottest path in the app.
    /// </summary>
    private readonly Dictionary<CoreWebView2, BrowserTab> _owners = [];

    /// <summary>
    /// Closed tabs, most recent first, for Ctrl+Shift+T. Bounded because this is
    /// an undo buffer and not a history feature -- an unbounded one would quietly
    /// retain every URL of the session with no way to clear it.
    /// </summary>
    private readonly List<ClosedTab> _closed = [];

    private const int ClosedTabMemory = 25;

    /// <summary>A tab that can be brought back: URL, title, and where it sat.</summary>
    private readonly record struct ClosedTab(string Url, string Title, int Index);

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

    /// <summary>Whether the full-screen tab wall is showing.</summary>
    public bool BigPicture { get; private set; }

    /// <summary>Per-host exemptions from content filtering.</summary>
    public SiteRules Rules { get; } = new();

    /// <summary>The mode this process is running in.</summary>
    public HearthMode Mode => _options.Profile.Mode;

    /// <summary>
    /// Raised when a page reports a navigation gesture -- the both-buttons swipe
    /// or a mouse back/forward button. Mouse input over web content never
    /// reaches WPF (k.mouse-input-never-reaches-wpf), so it arrives here as a
    /// web message from an injected listener instead.
    /// </summary>
    public event EventHandler<string>? Gesture;

    /// <summary>
    /// Snapshot of the tab list for <see cref="SessionStore"/>. Ids are carried
    /// so a restored tab still owns its screenshot on disk.
    /// </summary>
    public SessionState CaptureSession() => new()
    {
        Mode = Mode,
        Tabs = _tabs.Select(t => new SessionTab
        {
            Id = t.Id,
            Url = t.Url,
            Title = t.Title,
            IsActive = ReferenceEquals(t, Active)
        }).ToList()
    };

    /// <summary>
    /// Rebuilds tabs from a saved session without activating any of them. Cold
    /// is the correct restored state: reopening 40 tabs must cost 40 URLs, not
    /// 40 renderers, or restore would violate the one thesis the project has
    /// (c.hibernate-by-default). The caller activates exactly one.
    /// </summary>
    public BrowserTab? RestoreSession(SessionState state)
    {
        BrowserTab? active = null;

        foreach (var saved in state.Tabs)
        {
            var tab = new BrowserTab(saved.Url, saved.Id) { Title = saved.Title };

            // A snapshot from the previous run is already on disk under this id,
            // so adopt it: the grid comes back populated rather than blank.
            Snapshots.Adopt(tab);

            _tabs.Add(tab);
            if (saved.IsActive) active = tab;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return active ?? _tabs.FirstOrDefault();
    }

    /// <summary>
    /// Raised when a tab has just been given a brand-new WebView2, so the shell
    /// can hook things that live on the control rather than on the tab -- the
    /// keyboard path in <see cref="ShortcutRouter"/> above all. Firing this only
    /// for genuinely new controls, not for resumes, keeps handlers from stacking
    /// up on a tab that is activated repeatedly.
    /// </summary>
    public event EventHandler<BrowserTab>? Realised;

    /// <summary>
    /// The budget actually in force. Big Picture pins it to one: you are looking
    /// at a wall of pictures, so exactly one page needs to be real. The lean-back
    /// aesthetic and the memory architecture want the same thing here, which is
    /// the reason this mode is worth having at all rather than being a skin.
    /// </summary>
    public int EffectiveBudget => BigPicture ? 1 : _options.LiveTabBudget;

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
    /// Puts every tab except the active one to sleep, regardless of budget.
    ///
    /// Called on the way OUT of Big Picture. Leaving the wall is the clearest
    /// signal the user has finished surveying and picked one thing, so the rest
    /// should go cold rather than linger until some later activation happens to
    /// push them past the ceiling. Eviction that waits for pressure is the
    /// reactive behaviour this project exists to avoid (c.hibernate-by-default).
    /// </summary>
    public async Task HibernateAllButActiveAsync()
    {
        await _budgetGate.WaitAsync();
        try
        {
            var now = DateTime.UtcNow;
            foreach (var tab in EvictionPolicy
                         .EvictionOrder(_tabs, Active, now, _options.Profile.EvictByRecencyOnly)
                         .ToList())
                await HibernateAsync(tab);
        }
        finally
        {
            _budgetGate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Exempts the active tab's host from content filtering and reloads it, so
    /// the thing the user was trying to do actually works on the retry rather
    /// than on their next visit. Filtering only affects requests made from now
    /// on, so without the reload the grant would appear to do nothing.
    /// </summary>
    public void AllowActiveSite()
    {
        if (Active is not { } tab) return;

        Rules.Allow(SiteRules.HostOf(tab.Url));
        tab.BlockedCount = 0;
        tab.View?.CoreWebView2?.Reload();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Installs the request filter on a realised tab. Registering a filter is
    /// idempotent; the handler is what actually decides anything, and it defers
    /// to <see cref="SiteRules"/> per request rather than being torn down and
    /// rebuilt when a rule changes.
    /// </summary>
    private void ApplyContentPolicy(CoreWebView2 core)
    {
        core.WebResourceRequested -= OnWebResourceRequested;
        if (!_options.BlockThirdPartyFrames) return;

        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Document);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Media);
        core.WebResourceRequested += OnWebResourceRequested;
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (sender is not CoreWebView2 core) return;
        if (!_owners.TryGetValue(core, out var tab)) return;

        // A host the user has explicitly unlocked loads exactly as it would in
        // any other browser. Checked per request rather than per navigation so a
        // grant takes effect on the reload that follows it.
        if (Rules.IsAllowed(SiteRules.HostOf(tab.Url))) return;

        // Ask Chromium what the request is, instead of inferring it.
        //
        // The first version of this compared the request's host against
        // core.Source, and it blocked the top-level navigation of every tab:
        // when the very first document request goes out, core.Source is still
        // about:blank, so "differs from the current page" is trivially true.
        // Filtering was previously only ever switched on mid-session, after a
        // page had committed, which hid the bug completely until 0008 made it
        // the default (k.source-is-stale-during-navigation).
        //
        // Sec-Fetch-Dest and Sec-Fetch-Site are computed by the engine on every
        // request and do not depend on any state we have to track: Dest says
        // what the request is for, Site says how far it reaches. A top-level
        // navigation is dest=document, and it is never refused.
        var dest = Header(e, "Sec-Fetch-Dest");
        var site = Header(e, "Sec-Fetch-Site");

        if (dest is not null)
        {
            if (dest is "document") return;

            var crossSite = site is "cross-site";
            var isSubframe = dest is "iframe" or "frame";
            var isMedia = dest is "audio" or "video"
                          || e.ResourceContext == CoreWebView2WebResourceContext.Media;

            // Only cross-site media is refused. Same-origin media costs no extra
            // renderer -- the 0005 win came from frames, proven by that commit's
            // control run -- so blocking a page's own audio player would break
            // sites for nothing.
            if ((isSubframe || isMedia) && crossSite) Refuse(e, tab, core);
            return;
        }

        // Fallback for requests that carry no Sec-Fetch headers. tab.Url is the
        // authoritative top-level URL here, kept current by NavigationStarting;
        // core.Source is not (see above).
        if (e.ResourceContext is not (CoreWebView2WebResourceContext.Document
            or CoreWebView2WebResourceContext.Media)) return;

        if (!Uri.TryCreate(tab.Url, UriKind.Absolute, out var top)) return;
        if (string.IsNullOrEmpty(top.Host)) return;
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var requested)) return;
        if (string.Equals(requested.Host, top.Host, StringComparison.OrdinalIgnoreCase)) return;

        // Never block the top-level navigation itself.
        if (string.Equals(e.Request.Uri, tab.Url, StringComparison.OrdinalIgnoreCase)) return;

        Refuse(e, tab, core);
    }

    private static string? Header(CoreWebView2WebResourceRequestedEventArgs e, string name)
    {
        try
        {
            return e.Request.Headers.Contains(name) ? e.Request.Headers.GetHeader(name) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Declines a request and tells the owning tab it happened. The count is
    /// what the shield binds to -- a filter nobody can see is indistinguishable
    /// from a broken page (d.filtering-needs-an-escape-hatch).
    /// </summary>
    private void Refuse(
        CoreWebView2WebResourceRequestedEventArgs e, BrowserTab tab, CoreWebView2 core)
    {
        e.Response = core.Environment.CreateWebResourceResponse(null, 204, "Blocked", "");

        _contentHost.Dispatcher.BeginInvoke(() =>
        {
            tab.BlockedCount++;
            Changed?.Invoke(this, EventArgs.Empty);
        });
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

        // THIS IS THE ONLY MOMENT these switches can be set
        // (k.browser-args-fixed-at-creation). AdditionalBrowserArguments is read
        // when the browser process starts and never again, and WebView2 refuses
        // a second environment over the same user-data folder with different
        // options. That single fact is why changing mode restarts the process
        // rather than reconfiguring a live one.
        //
        // --renderer-process-limit is here too and does nothing, deliberately
        // left as a documented dead end (api.additional-browser-arguments).
        var environmentOptions = new CoreWebView2EnvironmentOptions();
        var args = options.BrowserArguments();
        if (args.Length > 0)
            environmentOptions.AdditionalBrowserArguments = args;

        Diag.Log($"environment: mode={options.Profile.Mode} budget={options.LiveTabBudget} "
               + $"filter={options.BlockThirdPartyFrames} args=[{args}]");

        return await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: folder,
            options: environmentOptions);
    }

    /// <summary>
    /// Adds a tab. Note it starts <see cref="TabState.Cold"/> and holds no
    /// renderer -- a tab only costs memory once it is actually activated. This
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
            // moment a snapshot can be taken -- by eviction time the tab has long
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
    /// reactively under memory pressure -- which is the difference between this
    /// and Chrome Memory Saver (c.hibernate-by-default).
    /// </summary>
    private async Task EnforceBudgetAsync()
    {
        var now = DateTime.UtcNow;
        var overBudget = LiveCount - EffectiveBudget;
        if (overBudget <= 0) return;

        foreach (var victim in EvictionPolicy
                     .EvictionOrder(_tabs, Active, now, _options.Profile.EvictByRecencyOnly)
                     .Take(overBudget))
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

        // No capture here -- by this point the tab was collapsed at blur and
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
            // TrySuspend legitimately returns false -- media playback, downloads
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
        _owners[core] = tab;
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

        // NavigationStarting fires only for the top-level document (frames go to
        // FrameNavigationStarting), so this is the earliest and most reliable
        // point at which the tab's own URL is known. The content filter reads it
        // to decide what counts as cross-site, and it has to be right BEFORE the
        // page's subresources start arriving.
        core.NavigationStarting += (_, e) =>
        {
            tab.Url = e.Uri;

            // A blocked-request count belongs to one page. Carried across a
            // navigation it would describe the previous page, and the shield
            // would offer to unlock a site no longer on screen.
            if (!e.IsRedirected) tab.BlockedCount = 0;
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

        // Navigation gestures are recognised inside the page, because mouse
        // input over web content never reaches WPF at all
        // (k.mouse-input-never-reaches-wpf).
        core.WebMessageReceived += (_, e) =>
        {
            string message;
            try { message = e.TryGetWebMessageAsString(); }
            catch { return; }   // Not one of ours; pages may post objects.

            if (message.StartsWith("hearth:", StringComparison.Ordinal))
                Gesture?.Invoke(this, message["hearth:".Length..]);
        };

        try
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(GestureScript.Source);
        }
        catch (Exception ex)
        {
            // Gestures are a convenience with a keyboard equivalent for every
            // action, so losing them must never cost the tab.
            Diag.Log($"gesture script injection failed: {ex.Message}");
        }

        core.Navigate(tab.Url);
        tab.State = TabState.Live;

        // Last, so handlers see a fully wired tab. This is where the keyboard
        // path gets attached to the new control (k.two-keyboard-paths); miss it
        // and shortcuts stop working the moment focus enters this tab.
        Realised?.Invoke(this, tab);
    }

    public void Navigate(BrowserTab tab, string url)
    {
        tab.Url = url;
        tab.View?.CoreWebView2?.Navigate(url);
    }

    public void Close(BrowserTab tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0 || !_tabs.Remove(tab)) return;

        _closed.Insert(0, new ClosedTab(tab.Url, tab.Title, index));
        if (_closed.Count > ClosedTabMemory) _closed.RemoveAt(_closed.Count - 1);

        Destroy(tab);
        Snapshots.Forget(tab.Id);

        if (ReferenceEquals(Active, tab))
        {
            Active = null;

            // The neighbour, not the last tab in the strip. Closing tab 3 of 9
            // and landing on tab 9 loses the user's place in whatever they were
            // working through; landing on tab 3 keeps it.
            var next = _tabs.ElementAtOrDefault(Math.Min(index, _tabs.Count - 1));
            if (next is not null) _ = ActivateAsync(next);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>True when there is anything for Ctrl+Shift+T to bring back.</summary>
    public bool CanReopenClosed => _closed.Count > 0;

    /// <summary>
    /// Reopens the most recently closed tab at the position it was closed from.
    /// Only URL and title come back -- the renderer was destroyed and its
    /// snapshot deliberately deleted with it, so this is a fresh load, not a
    /// restore. Claiming otherwise would be exactly the over-promise that makes
    /// people distrust every other tab suspender (p.lossy-restore).
    /// </summary>
    public BrowserTab? ReopenLastClosed()
    {
        if (_closed.Count == 0) return null;

        var entry = _closed[0];
        _closed.RemoveAt(0);

        var tab = new BrowserTab(entry.Url) { Title = entry.Title };
        _tabs.Insert(Math.Clamp(entry.Index, 0, _tabs.Count), tab);

        _ = ActivateAsync(tab);
        return tab;
    }

    /// <summary>
    /// Releases a tab's renderer and returns it to <see cref="TabState.Cold"/>.
    /// Disposing the control is what actually hands memory back to the OS;
    /// removing it from the visual tree alone does not.
    /// </summary>
    private void Destroy(BrowserTab tab)
    {
        if (tab.View is null) return;

        // Drop the ownership entry before disposing: the dictionary is keyed on
        // the CoreWebView2, and reading that property off a disposed control
        // throws.
        if (tab.View.CoreWebView2 is { } core) _owners.Remove(core);

        _contentHost.Children.Remove(tab.View);
        tab.View.Dispose();
        tab.View = null;
        tab.State = TabState.Cold;
        tab.BlockedCount = 0;

        // A rebuilt tab has not painted again yet, so it is not photographable
        // until its next navigation completes.
        tab.HasRendered = false;
    }
}
