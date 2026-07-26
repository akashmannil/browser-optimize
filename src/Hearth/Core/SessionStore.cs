using System.IO;
using System.Text.Json;

namespace Hearth.Core;

/// <summary>One tab as it survives a restart.</summary>
public sealed record SessionTab
{
    public required Guid Id { get; init; }
    public required string Url { get; init; }
    public required string Title { get; init; }
    public bool IsActive { get; init; }
}

/// <summary>The whole window, as it survives a restart.</summary>
public sealed record SessionState
{
    public HearthMode Mode { get; init; } = HearthMode.Browse;
    public List<SessionTab> Tabs { get; init; } = [];
    public DateTime SavedUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Persists the tab list across restarts, at store/session.json.
///
/// This exists because of k.browser-args-fixed-at-creation: switching modes has
/// to relaunch the process, and a browser that loses your tabs when you change a
/// setting is not one anybody would change the setting on. So session
/// persistence is not a bonus feature here, it is what makes the mode switch
/// affordable.
///
/// TAB IDS ARE PART OF THE PAYLOAD, and that is the whole trick. Snapshots are
/// stored as store/shots/{tabId}.png, so carrying the id across the restart
/// means the rebuilt tab finds its own last frame already on disk and the grid
/// comes back populated instead of blank. Restoring a session with fresh ids
/// would orphan every screenshot the previous run captured -- they would still
/// be on disk, just unreachable.
///
/// What comes back is a URL and a title. Not history, not form state, not scroll
/// (the snapshot's offset is only replayed for tabs torn down within a session).
/// Saying so plainly matters more than the feature does: p.lossy-restore is a
/// problem this project exists to take seriously, and quietly over-promising
/// restore is how the tools it criticises lost people's trust.
/// </summary>
public sealed class SessionStore
{
    private readonly string _path;

    public SessionStore() => _path = Path.Combine(App.StoreRoot, "session.json");

    public bool Exists => File.Exists(_path);

    public void Save(SessionState state)
    {
        try
        {
            // Write beside, then move over. A session file half-written when the
            // process exits is worse than none: it loses the tabs AND has to be
            // diagnosed, whereas an atomic replace can only ever leave the
            // previous good file in place.
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state,
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Diag.Log($"session save failed: {ex.Message}");
        }
    }

    public SessionState? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var state = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(_path));
            return state is { Tabs.Count: > 0 } ? state : null;
        }
        catch (Exception ex)
        {
            Diag.Log($"session load failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Removes the session file. Called once it has been consumed, so a crash
    /// during restore cannot put the browser into a loop reopening the same
    /// tabs that caused it.
    /// </summary>
    public void Clear()
    {
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch (Exception ex) { Diag.Log($"session clear failed: {ex.Message}"); }
    }
}
