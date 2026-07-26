using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Web.WebView2.Wpf;

namespace Hearth.Core;

/// <summary>
/// A tab. Deliberately NOT "a WebView2 with some metadata attached" -- the
/// identity of a tab is its URL, title and history, all of which survive the
/// renderer being thrown away. <see cref="View"/> is the disposable part.
///
/// This inversion is the model that makes eviction cheap to reason about:
/// tabs are documents, renderers are a cache (c.hibernate-by-default).
/// </summary>
public sealed class BrowserTab : INotifyPropertyChanged
{
    private const string DefaultTitle = "New tab";

    private string _title = DefaultTitle;
    private string _url;
    private TabState _state = TabState.Cold;
    private WebView2? _view;
    private string? _snapshotPath;
    private int _blockedCount;

    /// <param name="id">
    /// Supplied only when restoring a session. A tab's snapshot lives at
    /// shots/{id}.png, so carrying the id across a restart is what lets the
    /// rebuilt tab find its own last frame; a fresh id would orphan the file.
    /// </param>
    public BrowserTab(string url, Guid? id = null)
    {
        _url = url;
        Id = id ?? Guid.NewGuid();
        CreatedUtc = DateTime.UtcNow;
        LastActivatedUtc = DateTime.MinValue;
    }

    public Guid Id { get; }
    public DateTime CreatedUtc { get; }

    /// <summary>
    /// Drives LRU eviction ordering in commit 0003. DateTime.MinValue means
    /// the tab has never been focused -- those are the first eviction victims.
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

    /// <summary>
    /// How many cross-origin subframe and media requests the content filter has
    /// refused on this page's current navigation.
    ///
    /// This is the number the shield in the toolbar is bound to, and it is the
    /// whole reason filtering can be a default (see <see cref="SiteRules"/>):
    /// the filter announces what it did rather than leaving the user to work out
    /// why a login button does nothing. Reset on every navigation, because a
    /// count carried over from the previous page describes nothing.
    /// </summary>
    public int BlockedCount
    {
        get => _blockedCount;
        internal set => Set(ref _blockedCount, value);
    }

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

    /// <summary>
    /// The tab's short label in the strip.
    ///
    /// Falls back to the host when no real title is known. That case used to be
    /// rare -- only a background tab opened and never visited -- but session
    /// restore made it the common one: every restored tab is cold, so a whole
    /// strip of them would otherwise read "New tab", "New tab", "New tab".
    /// The host is what a person actually recognises a page by.
    /// </summary>
    public string DisplayLabel
    {
        get
        {
            var text = string.IsNullOrWhiteSpace(Title) || Title == DefaultTitle
                ? HostLabel
                : Title;

            return text.Length <= 24 ? text : text[..23] + "\u2026";
        }
    }

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

    /// <summary>Host name only -- what a person actually uses to recognise a page.</summary>
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
        if (name is nameof(Url))
        {
            OnPropertyChanged(nameof(HostLabel));

            // DisplayLabel falls back to the host, so it changes with the URL
            // too whenever the tab has no real title yet.
            OnPropertyChanged(nameof(DisplayLabel));
        }
        return true;
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public override string ToString() => $"{Id:N}[{State}] {Title} -- {Url}";
}
