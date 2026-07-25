namespace Hearth.Core;

/// <summary>
/// The lifecycle states a tab moves through. See docs/architecture.md.
///
/// The ordering is deliberate: increasing value means cheaper to hold and
/// slower to restore. Eviction always moves a tab to a HIGHER value.
/// </summary>
public enum TabState
{
    /// <summary>
    /// Realised and interactive. Holds a full renderer (~40-80 MB baseline
    /// before content). This is the state the budget in commit 0003 rations.
    /// </summary>
    Live = 0,

    /// <summary>
    /// Controller retained but hinted to minimise memory via
    /// MemoryUsageTargetLevel. Restores instantly.
    /// Implemented in commit 0003.
    /// </summary>
    Warm = 1,

    /// <summary>
    /// Suspended via TrySuspend(). Controller alive, renderer mostly released,
    /// restores in roughly 100 ms.
    /// Implemented in commit 0003.
    /// </summary>
    Hibernated = 2,

    /// <summary>
    /// No controller at all — the tab is a URL, a screenshot and serialised
    /// page state on disk. Costs essentially nothing in memory. This is the
    /// DEFAULT state for a tab the user is not circling back to, which is the
    /// inversion the whole project rests on (c.hibernate-by-default).
    /// Full teardown lands in commit 0004.
    /// </summary>
    Cold = 3
}
