namespace Hearth.Core;

/// <summary>
/// Every keyboard-reachable action, named once.
///
/// The point of naming them is that a command is not the same thing as a key.
/// Commit 0008 found that keys arrive by two completely different routes
/// depending on where focus happens to be (k.two-keyboard-paths), and the only
/// way to keep the two routes honest is for both to resolve to the same
/// vocabulary and hand it to the same dispatcher. A shortcut that works in the
/// address bar but not on a page is worse than one that works nowhere: it
/// teaches the user the wrong model.
/// </summary>
public enum BrowserCommand
{
    None,

    NewTab,
    CloseTab,
    ReopenClosedTab,

    FocusAddress,
    Reload,
    HardReload,
    Back,
    Forward,
    Home,

    NextTab,
    PreviousTab,

    /// <summary>Jump to a specific tab. Carries a zero-based index.</summary>
    SelectTab,
    SelectLastTab,

    ZoomIn,
    ZoomOut,
    ZoomReset,

    /// <summary>Show or hide the tab grid.</summary>
    ToggleGrid,

    /// <summary>
    /// Restart into, or out of, immersion mode. This one genuinely relaunches
    /// the process — see k.browser-args-fixed-at-creation.
    /// </summary>
    ToggleImmersion,

    /// <summary>
    /// Escape. Deliberately NOT bound unconditionally: Escape belongs to the
    /// page (stop loading, close a lightbox, leave a text field) unless a
    /// Hearth surface is up to receive it. The dispatcher decides, and declines
    /// the key when nothing is open, which lets it fall through to the page.
    /// </summary>
    Dismiss
}
