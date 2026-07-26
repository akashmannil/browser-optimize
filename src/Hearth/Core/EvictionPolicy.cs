namespace Hearth.Core;

/// <summary>
/// Decides which live tab loses its renderer when the budget binds.
///
/// Pure LRU is the obvious answer and it is wrong on its own: it cannot tell a
/// reference doc you reopen twenty times a day from an article you opened once
/// and abandoned. Both look identical the moment after you switch away. So
/// recency is weighted by how often the tab has actually been returned to
/// (c.tab-taxonomy).
///
/// This class is where c.refindability lands later -- a filtered dashboard view
/// or a half-filled form should resist eviction far harder than a homepage,
/// because the cost of being wrong is not a reload but a loss.
/// </summary>
public static class EvictionPolicy
{
    /// <summary>
    /// Higher score means more worth keeping live. The active tab is excluded by
    /// the caller rather than scored, since evicting what the user is looking at
    /// is never correct regardless of history.
    /// </summary>
    public static double Score(BrowserTab tab, DateTime nowUtc)
    {
        // Never activated: opened in the background and never visited. These are
        // the cheapest possible eviction victims -- the user has no working
        // context in them to lose.
        if (tab.LastActivatedUtc == DateTime.MinValue) return double.NegativeInfinity;

        var idleMinutes = (nowUtc - tab.LastActivatedUtc).TotalMinutes;

        // Recency decays rather than dropping off a cliff, so a tab isn't
        // condemned by a few seconds' difference in switch order.
        var recency = 1.0 / (1.0 + idleMinutes);

        // Revisit count is dampened: the difference between 1 and 5 visits is
        // meaningful, between 40 and 50 is noise.
        var habit = Math.Log(1 + tab.ActivationCount);

        return recency + 0.35 * habit;
    }

    /// <summary>
    /// Returns live tabs ordered worst-first -- the eviction queue.
    ///
    /// <paramref name="recencyOnly"/> drops the habit term and orders purely by
    /// when a tab was last looked at. Immersion asks for this because it wants
    /// "the last few in the chain": the tabs you just came from, in order, so
    /// stepping back and forth is always instant. Habit weighting is right for a
    /// working session -- a reference doc opened forty times should outrank
    /// something opened once -- but during a lean-back session it answers the
    /// wrong question, keeping the dashboard checked every morning alive instead
    /// of the thing being watched two tabs ago.
    /// </summary>
    public static IEnumerable<BrowserTab> EvictionOrder(
        IEnumerable<BrowserTab> tabs, BrowserTab? active, DateTime nowUtc,
        bool recencyOnly = false)
    {
        var candidates = tabs.Where(t => t.State == TabState.Live && !ReferenceEquals(t, active));

        return recencyOnly
            ? candidates.OrderBy(t => t.LastActivatedUtc)
            : candidates.OrderBy(t => Score(t, nowUtc));
    }
}
