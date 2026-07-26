using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Hearth.Core;

/// <summary>
/// Captured page state for a tab that no longer holds a renderer: a screenshot
/// plus enough scroll/form detail to put the user back where they were.
/// </summary>
public sealed record TabSnapshot
{
    public required string ImagePath { get; init; }
    public double ScrollX { get; init; }
    public double ScrollY { get; init; }
    public DateTime CapturedUtc { get; init; }
}

/// <summary>
/// Captures and persists tab snapshots. This is what makes eviction survivable:
/// an evicted tab renders its own last frame at its own scroll offset, so it is
/// visually indistinguishable from a live one (p.lossy-restore).
///
/// TIMING (k.capture-requires-live): capture MUST happen while the WebView2 is
/// still realised and has painted — i.e. at blur, immediately before eviction,
/// never after. There is no way to screenshot a controller that has been
/// disposed, so a missed capture is permanent for that tab.
/// </summary>
public sealed class SnapshotStore
{
    private readonly string _root;
    private readonly Dictionary<Guid, TabSnapshot> _snapshots = [];

    public SnapshotStore()
    {
        _root = Path.Combine(App.StoreRoot, "shots");
        Directory.CreateDirectory(_root);
    }

    public TabSnapshot? Get(Guid tabId) =>
        _snapshots.TryGetValue(tabId, out var s) ? s : null;

    public bool Has(Guid tabId) => _snapshots.ContainsKey(tabId);

    /// <summary>
    /// Re-adopts a screenshot left on disk by a previous run, for a tab restored
    /// from a session. Returns false when there is nothing to adopt, which is
    /// the ordinary case for a tab that was never blurred while live.
    ///
    /// The scroll offset is deliberately NOT restored here. It was captured in a
    /// process that has exited and was never persisted, so claiming it would be
    /// a lie about restore fidelity — precisely the over-promise that makes
    /// people distrust every other tab suspender (p.lossy-restore). The image is
    /// real and is worth having; the offset is not available and is not faked.
    /// </summary>
    public bool Adopt(BrowserTab tab)
    {
        var path = Path.Combine(_root, $"{tab.Id:N}.png");
        if (!File.Exists(path)) return false;

        try
        {
            if (new FileInfo(path).Length == 0) return false;
        }
        catch
        {
            return false;
        }

        _snapshots[tab.Id] = new TabSnapshot
        {
            ImagePath = path,
            CapturedUtc = File.GetLastWriteTimeUtc(path)
        };

        tab.SnapshotPath = $"{path}?t={DateTime.UtcNow.Ticks}";
        return true;
    }

    /// <summary>
    /// Screenshots the tab and records its scroll offset. Returns false if the
    /// tab could not be captured, in which case the caller should prefer
    /// suspension over teardown — evicting a tab we cannot repaint is exactly
    /// the papercut this class exists to prevent.
    /// </summary>
    public async Task<bool> CaptureAsync(BrowserTab tab)
    {
        if (tab.View?.CoreWebView2 is not { } core) return false;

        // A tab blurred before its first paint photographs as a blank frame, and
        // a blank thumbnail reads as a broken page rather than an unvisited one.
        // Refusing the capture leaves the card showing its host name, which is
        // honest. It also correctly withholds permission to tear the tab down.
        if (!tab.HasRendered) return false;

        try
        {
            var scroll = await ReadScrollAsync(core);
            var path = Path.Combine(_root, $"{tab.Id:N}.png");

            // Buffer in memory first. Writing the stream straight to a FileStream
            // leaves a 0-byte file behind whenever the capture yields nothing,
            // and a 0-byte PNG is indistinguishable from a real snapshot at the
            // call site — it would license a teardown we cannot repaint from.
            using var buffer = new MemoryStream();
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, buffer);
            if (buffer.Length == 0) return false;

            await File.WriteAllBytesAsync(path, buffer.ToArray());

            _snapshots[tab.Id] = new TabSnapshot
            {
                ImagePath = path,
                ScrollX = scroll.x,
                ScrollY = scroll.y,
                CapturedUtc = DateTime.UtcNow
            };

            // Stamped with the capture time so bindings see a changed value and
            // reload the file; the path alone is stable and would not invalidate.
            tab.SnapshotPath = $"{path}?t={DateTime.UtcNow.Ticks}";
            return true;
        }
        catch (Exception ex)
        {
            // A capture can fail for ordinary reasons: the page navigated away
            // mid-call, or the controller was torn down by a racing close.
            Debug.WriteLine($"[hearth] capture failed for {tab.Url}: {ex.Message}");
            return false;
        }
    }

    private static async Task<(double x, double y)> ReadScrollAsync(CoreWebView2 core)
    {
        try
        {
            var json = await core.ExecuteScriptAsync(
                "JSON.stringify([window.scrollX,window.scrollY])");

            // ExecuteScriptAsync returns a JSON-encoded string, so the array
            // arrives double-encoded and needs unwrapping twice.
            var inner = JsonSerializer.Deserialize<string>(json);
            if (inner is null) return (0, 0);

            var pair = JsonSerializer.Deserialize<double[]>(inner);
            return pair is { Length: 2 } ? (pair[0], pair[1]) : (0, 0);
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// Replays a snapshot's scroll offset once the page has finished loading.
    /// Without this a rehydrated tab silently returns to the top of the page,
    /// which is the single most-noticed restore failure in every other
    /// suspender.
    /// </summary>
    public async Task RestoreScrollAsync(BrowserTab tab)
    {
        if (tab.View?.CoreWebView2 is not { } core) return;
        if (Get(tab.Id) is not { } snap) return;
        if (snap.ScrollX == 0 && snap.ScrollY == 0) return;

        try
        {
            await core.ExecuteScriptAsync(
                $"window.scrollTo({snap.ScrollX.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
                $"{snap.ScrollY.ToString(System.Globalization.CultureInfo.InvariantCulture)});");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[hearth] scroll restore failed: {ex.Message}");
        }
    }

    public void Forget(Guid tabId)
    {
        if (!_snapshots.Remove(tabId, out var snap)) return;
        try { if (File.Exists(snap.ImagePath)) File.Delete(snap.ImagePath); }
        catch (Exception ex) { Debug.WriteLine($"[hearth] snapshot delete failed: {ex.Message}"); }
    }

    /// <summary>Total bytes on disk — the "disk" half of the RAM-for-disk trade.</summary>
    public long DiskBytes()
    {
        long total = 0;
        foreach (var snap in _snapshots.Values)
        {
            try { total += new FileInfo(snap.ImagePath).Length; } catch { }
        }
        return total;
    }
}
