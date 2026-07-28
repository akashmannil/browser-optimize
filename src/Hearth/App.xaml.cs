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
    /// Launches the replacement process, and returns whether it started.
    ///
    /// SEPARATE FROM THE EXIT, and the gap between them is the point. The
    /// successor takes a second or more to put a window on screen, and until it
    /// does there is nothing where the browser used to be -- filmed across an
    /// F11, that showed as the desktop appearing for roughly half a second in
    /// the middle of what is meant to be one transition. Starting it while this
    /// process is still holding its curtain up spends that time in parallel
    /// instead of in series (d.the-restart-is-a-transition).
    ///
    /// The successor cannot create its environment until this process actually
    /// dies and releases the user-data folder (k.browser-args-fixed-at-creation),
    /// so it waits for the handover; overlapping the two is safe precisely
    /// because that wait already existed.
    ///
    /// The caller must have written the session first: everything in memory is
    /// about to be discarded.
    /// </summary>
    public static bool StartSuccessor(HearthMode mode)
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            Diag.Log("restart aborted: no process path");
            return false;
        }

        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            start.ArgumentList.Add($"{ModeArgument}{mode.ToString().ToLowerInvariant()}");
            start.ArgumentList.Add($"--handover={Environment.ProcessId}");

            Diag.Log($"starting successor in {mode}");
            Process.Start(start);
            return true;
        }
        catch (Exception ex)
        {
            // A failure here leaves the user with the browser they already have,
            // which is the whole reason the launch comes before the exit.
            Diag.Log($"restart failed, staying put: {ex.Message}");
            return false;
        }
    }

    /// <summary>Exits, releasing the user-data folder to the successor.</summary>
    public static void HandOver()
    {
        Diag.Log("handing over");
        Current.Shutdown();
    }

    /// <summary>
    /// Waits until the process we are replacing has exited, so its browser
    /// process has released the shared user-data folder. Without this the new
    /// instance can win the race and fail to create its environment at all.
    ///
    /// ASYNCHRONOUS, and that is the whole point of the rewrite in 0015. The
    /// blocking version stalled the UI thread for as long as the old process
    /// took to die, which meant the incoming window could not paint -- so a mode
    /// switch showed an unpainted white rectangle, the shape Windows draws for
    /// an application that is not responding. The wait is the same length; it is
    /// now spent animating the startup curtain instead of frozen.
    /// </summary>
    public static async Task AwaitHandoverAsync()
    {
        var arg = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--handover=", StringComparison.Ordinal));
        if (arg is null || !int.TryParse(arg["--handover=".Length..], out var pid)) return;

        try
        {
            using var previous = Process.GetProcessById(pid);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            Diag.Log($"waiting for pid {pid} to exit");
            await previous.WaitForExitAsync(deadline.Token);
        }
        catch (ArgumentException)
        {
            // Already gone, which is the good case.
        }
        catch (OperationCanceledException)
        {
            // The old process outlived its welcome. Carry on and let environment
            // creation report the real problem if the folder is still locked.
            Diag.Log("handover timed out");
        }
        catch (Exception ex)
        {
            Diag.Log($"handover wait failed: {ex.Message}");
        }
    }
}
