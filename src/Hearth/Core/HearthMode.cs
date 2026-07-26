namespace Hearth.Core;

/// <summary>The two poles the browser runs at. There is no third.</summary>
public enum HearthMode
{
    /// <summary>
    /// Lean. Tight budget, content filtering on, tabs asleep by default. This is
    /// what the browser is unless told otherwise (d.lean-is-the-default).
    /// </summary>
    Browse,

    /// <summary>
    /// Immersive. The screen belongs to the page: no chrome, real fullscreen,
    /// a generous recency chain kept awake, filtering off, and Chromium started
    /// with switches that trade memory for smoothness.
    /// </summary>
    Immersion
}

/// <summary>
/// Everything that differs between the two modes, in one place.
///
/// WHY THE MODE SWITCH RESTARTS THE PROCESS (k.browser-args-fixed-at-creation).
/// <see cref="BrowserArguments"/> can only be supplied to
/// CoreWebView2Environment.CreateAsync, and the environment is created once per
/// process. Worse, WebView2 refuses a second environment over the same user-data
/// folder with different options -- the browser process is already running with
/// the old ones. So the flags that make immersion actually faster cannot be
/// applied to a live process at all.
///
/// That is not a workaround dressed up as a feature. It is why "reboot into
/// immersion" is the honest shape for this: the alternative is a mode that
/// claims to boost performance while changing nothing that matters. The cost is
/// a restart, and the price of the restart is paid down by
/// <see cref="SessionStore"/>, which had to exist anyway.
/// </summary>
public sealed record ModeProfile
{
    public required HearthMode Mode { get; init; }

    /// <summary>Tabs allowed to hold a renderer at once.</summary>
    public required int LiveBudget { get; init; }

    /// <summary>Whether cross-site subframes and media are refused.</summary>
    public required bool FilterContent { get; init; }

    /// <summary>
    /// Evict by pure recency rather than by recency weighted with revisit habit.
    ///
    /// Immersion wants "the last few in the chain" -- the tabs you just came
    /// from, in order, so back-and-forth is always instant. Habit weighting is
    /// right for a working session, where a reference doc opened forty times
    /// should outrank something visited once, but during a lean-back session it
    /// produces the wrong answer: it keeps the dashboard you check every morning
    /// alive instead of the episode you were watching two tabs ago.
    /// </summary>
    public required bool EvictByRecencyOnly { get; init; }

    /// <summary>Chromium switches, fixed for the lifetime of the process.</summary>
    public required string[] BrowserArguments { get; init; }

    public static ModeProfile For(HearthMode mode) =>
        mode == HearthMode.Immersion ? Immersion : Browse;

    public static readonly ModeProfile Browse = new()
    {
        Mode = HearthMode.Browse,
        LiveBudget = 3,
        FilterContent = true,
        EvictByRecencyOnly = false,

        // Deliberately empty. Browse is the control condition for every memory
        // number this project publishes, and a control run carrying switches
        // nobody measured is not a control run.
        BrowserArguments = []
    };

    public static readonly ModeProfile Immersion = new()
    {
        Mode = HearthMode.Immersion,
        LiveBudget = 5,
        FilterContent = false,
        EvictByRecencyOnly = true,

        BrowserArguments =
        [
            // Chromium throttles and eventually backgrounds windows it believes
            // are covered. In a fullscreen lean-back session that judgement is
            // both wrong and expensive -- it is what makes a video stutter after
            // it has been playing untouched for a minute.
            "--disable-features=CalculateNativeWinOcclusion",
            "--disable-backgrounding-occluded-windows",
            "--disable-renderer-backgrounding",
            "--disable-background-timer-throttling",

            // A tab switched away from in immersion is still a tab you are
            // coming back to in seconds.
            "--autoplay-policy=no-user-gesture-required",

            // Push compositing onto the GPU. These cost memory, which is exactly
            // the trade immersion exists to make and browse exists to refuse.
            "--enable-gpu-rasterization",
            "--enable-zero-copy",
            "--ignore-gpu-blocklist"
        ]
    };
}
