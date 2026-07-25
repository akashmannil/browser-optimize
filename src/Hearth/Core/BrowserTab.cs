using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Web.WebView2.Wpf;

namespace Hearth.Core;

/// <summary>
/// A tab. Deliberately NOT "a WebView2 with some metadata attached" — the
/// identity of a tab is its URL, title and history, all of which survive the
/// renderer being thrown away. <see cref="View"/> is the disposable part.
///
/// This inversion is the model that makes eviction cheap to reason about:
/// tabs are documents, renderers are a cache (c.hibernate-by-default).
/// </summary>
public sealed class BrowserTab : INotifyPropertyChanged
{
    private string _title = "New tab";
    private string _url;
    private TabState _state = TabState.Cold;
    private WebView2? _view;
    private string? _snapshotPath;

    public BrowserTab(string url)
    {
        _url = url;
        Id = Guid.NewGuid();
        CreatedUtc = DateTime.UtcNow;
        LastActivatedUtc = DateTime.MinValue;
    }

    public Guid Id { get; }
    public DateTime CreatedUtc { get; }

    /// <summary>
    /// Drives LRU eviction ordering in commit 0003. DateTime.MinValue means
    /// the tab has never been focused — those are the first eviction victims.
    /// </summary>
    public DateTime LastActivatedUtc { get; internal set; }

    /// <summary>
    /// Number of times the user has returned to this tab. A tab revisited
    /// repeatedly is a reference doc, not a read-later item, and should
    /// survive eviction pressure longer (c.tab-taxonomy).
    /// </summary>
    public int ActivationCount { get; internal set; }

    /// <summary>
    /// Whether this tab has completed a navigation since being realised, and so
    /// has something on screen worth photographing.
    ///
    /// Without this guard a tab blurred before its first paint is captured
    /// blank, and a blank thumbnail is worse than none: it looks like a broken
    /// page rather than an unvisited one. Reset on teardown, because a rebuilt
    /// tab has not painted again yet.
    /// </summary>
    public bool HasRendered { get; internal set; }

    public string Url
    {
        get => _url;
        internal set => Set(ref _url, value);
    }

    public string Title
    {
        get => _title;
        internal set => Set(ref _title, value);
    }

    public TabState State
    {
        get => _state;
        internal set
        {
            if (Set(ref _state, value)) OnPropertyChanged(nameof(IsLive));
        }
    }

    public bool IsLive => _state == TabState.Live;

    /// <summary>
    /// The realised WebView2, or null when the tab holds no renderer.
    /// Null is the expected steady state for most tabs, not an error.
    /// </summary>
    public WebView2? View
    {
        get => _view;
        internal set => Set(ref _view, value);
    }

    /// <summary>Host for the tab's short display label in the strip.</summary>
    public string DisplayLabel => Title.Length <= 24 ? Title : Title[..23] + "…";

    /// <summary>
    /// Path to this tab's most recent screenshot, or null if it has never been
    /// blurred while live. Set by <see cref="SnapshotStore"/>; exposed on the tab
    /// so views can bind to it directly rather than querying the store per frame.
    /// </summary>
    public string? SnapshotPath
    {
        get => _snapshotPath;
        internal set => Set(ref _snapshotPath, value);
    }

    /// <summary>Host name only — what a person actually uses to recognise a page.</summary>
    public string HostLabel =>
        Uri.TryCreate(Url, UriKind.Absolute, out var uri)
            ? uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                ? uri.Host[4..]
                : uri.Host
            : Url;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        if (name is nameof(Title)) OnPropertyChanged(nameof(DisplayLabel));
        if (name is nameof(Url)) OnPropertyChanged(nameof(HostLabel));
        return true;
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public override string ToString() => $"{Id:N}[{State}] {Title} — {Url}";
}
