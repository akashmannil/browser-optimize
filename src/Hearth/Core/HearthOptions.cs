namespace Hearth.Core;

/// <summary>
/// Tunables for the memory budget. Gathered in one place because these are the
/// numbers a user is ultimately meant to control — "you set a RAM ceiling and
/// the browser respects it" is the product promise, and it cannot be delivered
/// by values scattered through the codebase.
/// </summary>
public sealed record HearthOptions
{
    /// <summary>
    /// How many tabs may hold a full renderer at once. Activating a tab beyond
    /// this evicts the lowest-scoring live tab (see <see cref="EvictionPolicy"/>).
    ///
    /// 6 is chosen to sit just above the number of tabs a person actively cycles
    /// between. Too low and eviction thrashes on ordinary alt-tabbing; too high
    /// and the budget stops binding at realistic tab counts.
    /// </summary>
    public int LiveTabBudget { get; init; } = 6;

    /// <summary>
    /// Chromium's cap on concurrent renderer processes, passed via
    /// --renderer-process-limit (api.additional-browser-arguments).
    ///
    /// This exists because of k.site-isolation-multiplies-renderers: commit 0002
    /// measured 14 renderers for 5 tabs, since site isolation allocates a
    /// renderer per site instance including cross-origin iframes. Rationing tabs
    /// alone therefore does NOT ration memory — one widget-heavy page can blow
    /// the ceiling by itself.
    ///
    /// SECURITY TRADE-OFF, made deliberately and documented rather than
    /// defaulted silently: capping the pool forces cross-origin site instances to
    /// share renderers, which weakens site isolation — the mitigation for
    /// Spectre-class cross-origin reads — and widens the blast radius of a single
    /// renderer crash. This is the same shape of trade Firefox makes with
    /// dom.ipc.processCount, and it is acceptable HERE because Hearth is an
    /// experiment measuring a memory thesis, not a hardened daily driver.
    ///
    /// Set to null to keep Chromium's default (unbounded) behaviour.
    ///
    /// MEASURED RESULT (0003): this flag DOES NOT WORK for our purpose. It
    /// reaches the browser process — verified in its command line — and is then
    /// ignored: with a limit of 8, stackoverflow.com alone still ran 14
    /// renderers. Chromium treats the limit as a soft cap that site isolation is
    /// permitted to exceed, because a site-isolated cross-origin frame MUST get
    /// a dedicated process. Left in place, defaulted off, as a documented dead
    /// end so it is not rediscovered and retried.
    /// </summary>
    public int? RendererProcessLimit { get; init; }

    /// <summary>
    /// Collapses same-site frames onto one process per site, rather than one per
    /// site *instance*. Unlike <see cref="RendererProcessLimit"/> this genuinely
    /// reduces renderer count, because it changes the isolation granularity
    /// rather than asking Chromium to violate it.
    ///
    /// Off by default: it is a real weakening of the process boundary, and
    /// commit 0003 established that eviction alone already delivers the memory
    /// result without touching site isolation at all.
    /// </summary>
    public bool ProcessPerSite { get; init; }

    /// <summary>Assembles the Chromium switches for the shared environment.</summary>
    public string BrowserArguments()
    {
        var args = new List<string>();
        if (RendererProcessLimit is { } limit) args.Add($"--renderer-process-limit={limit}");
        if (ProcessPerSite) args.Add("--process-per-site");
        return string.Join(' ', args);
    }

    /// <summary>
    /// Whether an evicted tab is torn down completely (controller destroyed)
    /// rather than merely suspended. Teardown reclaims 53% against suspension's
    /// 35% on the same workload, so it carries the memory promise.
    ///
    /// ON by default since commit 0004: teardown is now gated on holding a
    /// snapshot to repaint from, so an evicted tab shows its own last frame
    /// instead of going blank. TabManager falls back to suspension whenever a
    /// capture fails, which keeps the guarantee honest without giving up the
    /// reclaim in the common case.
    /// </summary>
    public bool AllowFullTeardown { get; init; } = true;

    /// <summary>
    /// Live-tab budget while low-power mode is engaged. Deliberately tight: the
    /// point of the mode is that the machine stops being the browser's, and a
    /// budget of 2 still supports the read-here / reference-there pattern that
    /// is most of actual browsing.
    /// </summary>
    public int LowPowerBudget { get; init; } = 2;

    /// <summary>
    /// In low-power mode, refuse cross-origin subframe documents and media.
    ///
    /// This is the indirect answer to k.no-process-model-control. We cannot cap
    /// renderers directly — every Chromium switch is ignored — but renderer
    /// count is a function of page CONTENT, and content is something an embedder
    /// absolutely can refuse. Cross-origin iframes are what fan a single tab out
    /// into a dozen renderers, so declining them should cut process count
    /// without any process-model API at all.
    /// </summary>
    public bool BlockThirdPartyFrames { get; init; } = true;

    /// <summary>Engage low-power mode at startup. Set by the benchmark harness.</summary>
    public bool StartInLowPower { get; init; }

    public static HearthOptions Default { get; } = new();

    /// <summary>
    /// Reads overrides from the environment so memory configurations can be A/B
    /// tested from a benchmark script without a rebuild. Comparing builds rather
    /// than runs would make the numbers non-comparable, which is exactly the
    /// mistake commit 0002 made in a different form.
    ///
    ///   HEARTH_LIVE_BUDGET=6   HEARTH_RENDERER_LIMIT=8   HEARTH_PROCESS_PER_SITE=1
    /// </summary>
    public static HearthOptions FromEnvironment()
    {
        var options = Default;

        if (int.TryParse(Environment.GetEnvironmentVariable("HEARTH_LIVE_BUDGET"), out var budget)
            && budget > 0)
            options = options with { LiveTabBudget = budget };

        if (int.TryParse(Environment.GetEnvironmentVariable("HEARTH_RENDERER_LIMIT"), out var limit)
            && limit > 0)
            options = options with { RendererProcessLimit = limit };

        if (Environment.GetEnvironmentVariable("HEARTH_PROCESS_PER_SITE") is "1" or "true")
            options = options with { ProcessPerSite = true };

        // Bidirectional: a flag that can only be switched ON cannot be A/B
        // tested against its own absence on a single build, and comparing across
        // builds is how commit 0002 produced a number it had to retract.
        if (Flag("HEARTH_FULL_TEARDOWN") is { } teardown)
            options = options with { AllowFullTeardown = teardown };

        if (Flag("HEARTH_BLOCK_FRAMES") is { } blockFrames)
            options = options with { BlockThirdPartyFrames = blockFrames };

        if (int.TryParse(Environment.GetEnvironmentVariable("HEARTH_LOW_POWER_BUDGET"),
                out var lpBudget) && lpBudget > 0)
            options = options with { LowPowerBudget = lpBudget };

        if (Flag("HEARTH_LOW_POWER") is true)
            options = options with { StartInLowPower = true };

        return options;
    }

    private static bool? Flag(string name) =>
        Environment.GetEnvironmentVariable(name) switch
        {
            "1" or "true" or "TRUE" => true,
            "0" or "false" or "FALSE" => false,
            _ => null
        };
}
