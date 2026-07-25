using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Hearth.Core;

/// <summary>
/// Hosts the user has explicitly told Hearth to load in full.
///
/// WHY THIS HAD TO EXIST BEFORE FILTERING COULD BE THE DEFAULT
/// (d.filtering-needs-an-escape-hatch). Commit 0005 measured that refusing
/// cross-origin subframes cuts one stackoverflow.com tab from 14 renderers to
/// 1, and commit 0008 makes that the default rather than a mode. But the same
/// filter also removes OAuth and SSO logins, payment frames, CAPTCHAs and
/// embedded players (k.frame-blocking-breaks-pages). Shipping that as a silent
/// default without recourse would not produce a lean browser, it would produce
/// one that cannot log into anything -- and the user's conclusion would be that
/// the browser is broken, which is the correct conclusion.
///
/// So the filter stays on, but it is never silent: the tab counts what it
/// refused, the shell surfaces that count, and one click records the host here
/// permanently. The cost of the escape hatch is per-host and paid once; the
/// cost of not having one is that the mode gets switched off entirely.
/// </summary>
public sealed class SiteRules
{
    private readonly string _path;
    private readonly HashSet<string> _allowed;

    public SiteRules()
    {
        _path = Path.Combine(App.StoreRoot, "site-rules.json");
        _allowed = Load(_path);
    }

    /// <summary>Whether this host is exempt from content filtering.</summary>
    public bool IsAllowed(string? host) =>
        !string.IsNullOrEmpty(host) && _allowed.Contains(Normalise(host));

    public int Count => _allowed.Count;

    /// <summary>Exempt a host from filtering, permanently, and persist it.</summary>
    public void Allow(string? host)
    {
        if (string.IsNullOrEmpty(host)) return;
        if (_allowed.Add(Normalise(host))) Save();
    }

    /// <summary>Put a host back under the filter.</summary>
    public void Block(string? host)
    {
        if (string.IsNullOrEmpty(host)) return;
        if (_allowed.Remove(Normalise(host))) Save();
    }

    /// <summary>
    /// www is stripped so allowing a site from one page covers the whole site.
    /// A rule the user has to grant twice for what they consider one site reads
    /// as the feature not working.
    /// </summary>
    private static string Normalise(string host)
    {
        host = host.Trim().ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    /// <summary>Host of a URL, normalised, or null if it is not a real URL.</summary>
    public static string? HostOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Normalise(uri.Host) : null;

    private static HashSet<string> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new HashSet<string>(StringComparer.Ordinal);
            var hosts = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path));
            return new HashSet<string>(hosts ?? [], StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            // A corrupt rules file must not stop the browser starting. Losing
            // the exemptions costs one extra click per site; refusing to launch
            // costs the session.
            Debug.WriteLine($"[hearth] site rules load failed: {ex.Message}");
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(
                _allowed.OrderBy(h => h, StringComparer.Ordinal).ToArray(),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[hearth] site rules save failed: {ex.Message}");
        }
    }
}
