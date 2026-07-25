using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Hearth.Views;

/// <summary>
/// Keeps a custom-chrome window inside the work area when maximised.
///
/// A WindowStyle="None" window maximises to the full monitor bounds, not the
/// work area, so it covers the taskbar and silently swallows whatever sits at
/// the bottom of the layout — here, the memory readout. Windows only asks about
/// this once, via WM_GETMINMAXINFO, so the correct size has to be supplied in
/// the answer rather than corrected afterwards.
///
/// The monitor is resolved per-window rather than assuming the primary, so this
/// stays right on multi-monitor setups with a taskbar on a secondary display.
/// </summary>
public static class MaximiseFix
{
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int MONITOR_DEFAULTTONEAREST = 0x00000002;

    public static void Attach(Window window)
    {
        if (PresentationSource.FromVisual(window) is HwndSource existing)
        {
            existing.AddHook(Hook);
            return;
        }

        window.SourceInitialized += (_, _) =>
        {
            var source = (HwndSource)PresentationSource.FromVisual(window)!;
            source.AddHook(Hook);
        };
    }

    private static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

        // Work area, expressed relative to the monitor's own origin — the values
        // are monitor-local, so a secondary display at a negative offset still
        // lands correctly.
        mmi.ptMaxPosition.x = info.rcWork.left - info.rcMonitor.left;
        mmi.ptMaxPosition.y = info.rcWork.top - info.rcMonitor.top;
        mmi.ptMaxSize.x = info.rcWork.right - info.rcWork.left;
        mmi.ptMaxSize.y = info.rcWork.bottom - info.rcWork.top;

        Marshal.StructureToPtr(mmi, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }
}
