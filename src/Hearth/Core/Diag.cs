using System.Diagnostics;
using System.IO;

namespace Hearth.Core;

/// <summary>
/// Opt-in trace log, written to store/trace.log when HEARTH_TRACE=1.
///
/// This exists because commit 0008's central claim -- that a keystroke landing
/// on a web page reaches the shell -- cannot be checked by reading the code or
/// by looking at the window. Both keyboard paths look identical from outside,
/// and the failure mode is silence. The house rule is that claims are measured
/// rather than estimated (see CLAUDE.md), and this is the instrument that makes
/// the keyboard claim measurable: drive a real key, read the log, see which
/// path carried it.
///
/// Off unless asked for, and appended to a file under store/ so it is covered by
/// the existing "delete store/ to reset" promise.
/// </summary>
public static class Diag
{
    private static readonly object Gate = new();
    private static readonly bool Enabled =
        Environment.GetEnvironmentVariable("HEARTH_TRACE") is "1" or "true";

    private static readonly Lazy<string> Path = new(() =>
        System.IO.Path.Combine(App.StoreRoot, "trace.log"));

    public static void Log(string message)
    {
        Debug.WriteLine($"[hearth] {message}");
        if (!Enabled) return;

        try
        {
            lock (Gate)
            {
                File.AppendAllText(Path.Value,
                    $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Tracing must never be the reason the browser misbehaves.
        }
    }
}
