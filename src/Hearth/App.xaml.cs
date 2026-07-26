using System.Diagnostics;
using System.IO;
using System.Windows;
using Hearth.Core;

namespace Hearth;

public partial class App : Application
{
    /// <summary>
    /// Root folder for everything Hearth persists: the shared WebView2 user-data
    /// folder, hibernation screenshots, and the session index.
    /// Kept beside the executable so a portfolio checkout stays self-contained.
    /// </summary>
    public static string StoreRoot { get; } = InitStoreRoot();

    /// <summary>
    /// The mode this process was launched in. Fixed for the process lifetime --
    /// see k.browser-args-fixed-at-creation for why it cannot be otherwise.
    /// </summary>
    public static HearthMode Mode { get; } = ReadMode();

    private const string ModeArgument = "--mode=";

    private static string InitStoreRoot()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "store");
        Directory.CreateDirectory(root);
        return root;
    }

    private static HearthMode ReadMode()
    {
        foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
        {
            if (!arg.StartsWith(ModeArgument, StringComparison.OrdinalIgnoreCase)) continue;
            if (arg[ModeArgument.Length..].Equals("immersion", StringComparison.OrdinalIgnoreCase))
                return HearthMode.Immersion;
        }

        return HearthMode.Browse;
    }

    /// <summary>URLs passed on the command line, with our own switches removed.</summary>
    public static string[] StartupUrls() =>
        Environment.GetCommandLineArgs()
            .Skip(1)
            .Where(a => !a.StartsWith("--", StringComparison.Ordinal))
            .ToArray();

    /// <summary>
    /// Relaunches Hearth in the other mode and exits this process.
    ///
    /// The caller must have already written the session, because everything
    /// in memory is about to be discarded. The new process is started BEFORE
    /// shutting down so a failure to launch leaves the user with the browser
    /// they already had rather than with nothing.
    ///
    /// The old process must actually exit and release the user-data folder: a
    /// second environment cannot be created over it while the first browser
    /// process is alive (k.browser-args-fixed-at-creation), which is the same
    /// constraint that makes the restart necessary in the first place. The new
    /// instance waits for the handover rather than racing it.
    /// </summary>
    public static void RestartInto(HearthMode mode)
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            Diag.Log("restart aborted: no process path");
            return;
        }

        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            start.ArgumentList.Add($"{ModeArgument}{mode.ToString().ToLowerInvariant()}");
            start.ArgumentList.Add($"--handover={Environment.ProcessId}");

            Diag.Log($"restarting into {mode}");
            Process.Start(start);
        }
        catch (Exception ex)
        {
            Diag.Log($"restart failed, staying put: {ex.Message}");
            return;
        }

        Current.Shutdown();
    }

    /// <summary>
    /// Blocks until the process we are replacing has exited, so its browser
    /// process has released the shared user-data folder. Without this the new
    /// instance can win the race and fail to create its environment at all.
    /// </summary>
    public static void AwaitHandover()
    {
        var arg = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--handover=", StringComparison.Ordinal));
        if (arg is null || !int.TryParse(arg["--handover=".Length..], out var pid)) return;

        try
        {
            using var previous = Process.GetProcessById(pid);
            Diag.Log($"waiting for pid {pid} to exit");
            previous.WaitForExit(10_000);
        }
        catch (ArgumentException)
        {
            // Already gone, which is the good case.
        }
        catch (Exception ex)
        {
            Diag.Log($"handover wait failed: {ex.Message}");
        }
    }
}
