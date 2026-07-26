using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Hearth.Views;

/// <summary>
/// Keeps a custom-chrome window inside the work area when maximised.
///
/// A WindowStyle="None" window maximises to the full monitor bounds, not the
/// work area, so it covers the taskbar and silently swallows whatever sits at
/// the bottom of the layout -- here, the memory readout. Windows only asks about
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

    /// <summary>
    /// Sizes a window to cover its entire monitor, taskbar included.
    ///
    /// MAXIMISED IS NOT FULLSCREEN, and that distinction is the whole of this
    /// method (k.maximised-is-not-fullscreen). Setting WindowState.Maximized and
    /// answering WM_GETMINMAXINFO with the monitor bounds does produce a window
    /// measuring exactly 1920x1080 at 0,0 -- verified down through every WebView2
    /// child HWND -- and the taskbar still draws on top of it. To the shell, a
    /// maximised window is by definition one that respects the work area, so it
    /// is never a candidate for the fullscreen treatment that hides the taskbar.
    ///
    /// A real fullscreen window is WindowState.Normal, topmost, sized explicitly
    /// to the monitor. Then the shell recognises it and yields.
    /// </summary>
    public static void Fullscreen(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();

        var monitor = MonitorFromWindow(handle, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        // Monitor bounds are physical pixels; Left/Top/Width/Height are DIPs.
        // Skipping this conversion looks correct at 100% scaling and leaves a
        // strip of desktop showing on every other machine.
        var dpi = VisualTreeHelper.GetDpi(window);

        window.WindowState = WindowState.Normal;
        window.Topmost = true;
        window.Left = (info.rcMonitor.left) / dpi.DpiScaleX;
        window.Top = (info.rcMonitor.top) / dpi.DpiScaleY;
        window.Width = (info.rcMonitor.right - info.rcMonitor.left) / dpi.DpiScaleX;
        window.Height = (info.rcMonitor.bottom - info.rcMonitor.top) / dpi.DpiScaleY;
    }

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

        // Work area, expressed relative to the monitor's own origin -- the values
        // are monitor-local, so a secondary display at a negative offset still
        // lands correctly. Immersion does not come through here at all; it uses
        // Fullscreen above, for the reason documented there.
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
