using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Hearth.Core;

/// <summary>
/// One keyboard map, delivered down two entirely separate paths.
///
/// WHY THIS CLASS EXISTS (k.two-keyboard-paths). Up to commit 0007 the shell
/// handled keys in Window.PreviewKeyDown and nothing worked, because that event
/// almost never fires. WebView2 is an out-of-process control: the HWND that
/// actually holds keyboard focus is created by msedgewebview2.exe, so its
/// WM_KEYDOWN messages are queued to THAT process's thread. Our message loop
/// never sees them, which rules out ComponentDispatcher and every other
/// thread-level filter as well. The moment focus lands on a page -- which is
/// the entire time anyone is browsing -- WPF is deaf.
///
/// The runtime's answer is CoreWebView2Controller.AcceleratorKeyPressed, which
/// forwards key presses back to the embedder. The WPF wrapper does not expose
/// the controller (k.wpf-wrapper-hides-controller), so it is reached by
/// reflection, guarded and fail-soft: if the lookup ever breaks, shortcuts
/// degrade to chrome-only rather than the app failing to start.
///
/// So: <see cref="HandleWpfKey"/> serves chrome focus, <see cref="Attach"/>
/// serves page focus, and both funnel through the same <see cref="Map"/> table
/// into the same dispatcher. There is exactly one place where a key becomes a
/// command.
/// </summary>
public sealed class ShortcutRouter
{
    /// <summary>
    /// Returns true if the command was consumed. Returning false lets the key
    /// through to the page, which is what makes Escape and the zoom keys behave
    /// correctly when no Hearth surface wants them.
    /// </summary>
    private readonly Func<BrowserCommand, int, bool> _dispatch;

    public ShortcutRouter(Func<BrowserCommand, int, bool> dispatch) => _dispatch = dispatch;

    /// <summary>True once at least one WebView2 controller was hooked successfully.</summary>
    public bool NativeHookWorking { get; private set; }

    /// <summary>Set when reflection failed, for the startup diagnostic.</summary>
    public string? NativeHookError { get; private set; }

    // -- Path 1: chrome focus (address bar, tab strip, grid) -----------------

    /// <summary>
    /// Call from Window.PreviewKeyDown. Note the Key.System unwrap: WPF reports
    /// every Alt combination as Key.System and hides the real key in SystemKey,
    /// so Alt+Left silently matched nothing before commit 0008.
    /// </summary>
    public bool HandleWpfKey(KeyEventArgs e)
    {
        // BOTH paths see the same keystroke when focus is in a page: the WPF
        // wrapper re-raises the key into the WPF tree *and* the controller
        // reports it natively. Measured in 0008 -- a single Ctrl+T logged on
        // both paths 4 ms apart and opened two tabs.
        //
        // OriginalSource is the only discriminator that actually works here.
        // The obvious candidates both lie while a page holds focus:
        // IsKeyboardFocusWithin reports False, and Keyboard.FocusedElement is
        // null. Neither is a bug -- WPF genuinely does not have focus -- which
        // is exactly why neither can be used to detect that it does not.
        if (NativeHookWorking && OriginatesInWebView(e.OriginalSource)) return false;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var (command, index) = Map(key, Keyboard.Modifiers);
        if (command == BrowserCommand.None) return false;

        var handled = _dispatch(command, index);
        Diag.Log($"key chrome  {Keyboard.Modifiers}+{key} -> {command} handled={handled}");
        return handled;
    }

    private static bool OriginatesInWebView(object? source)
    {
        if (source is WebView2) return true;

        // Walk up in case a future layout wraps the control: the check must
        // answer "did this come from web content", not "is this that control".
        for (var node = source as DependencyObject;
             node is not null;
             node = node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : null)
        {
            if (node is WebView2) return true;
        }

        return false;
    }

    // -- Path 2: page focus (inside the WebView2) ----------------------------

    /// <summary>
    /// Hooks a realised WebView2 so keys pressed while the page has focus reach
    /// the shell. Safe to call on every tab; failures are logged, never thrown.
    /// </summary>
    public void Attach(WebView2 view)
    {
        if (TryGetController(view) is not { } controller)
        {
            Diag.Log($"hook FAILED: {NativeHookError}");
            return;
        }

        controller.AcceleratorKeyPressed -= OnAcceleratorKeyPressed;
        controller.AcceleratorKeyPressed += OnAcceleratorKeyPressed;

        if (!NativeHookWorking) Diag.Log("hook attached: page-level shortcuts are live");
        NativeHookWorking = true;
    }

    private void OnAcceleratorKeyPressed(
        object? sender, CoreWebView2AcceleratorKeyPressedEventArgs e)
    {
        // Key-up would fire the command a second time for every press.
        if (e.KeyEventKind is not (CoreWebView2KeyEventKind.KeyDown
            or CoreWebView2KeyEventKind.SystemKeyDown)) return;

        var key = KeyInterop.KeyFromVirtualKey((int)e.VirtualKey);
        var mods = NativeModifiers();
        var (command, index) = Map(key, mods);
        if (command == BrowserCommand.None) return;

        // Handled must be set for anything we act on, or Chromium runs its own
        // binding too: Ctrl+R would reload twice, and Ctrl+W would race our
        // close against the runtime's.
        var handled = _dispatch(command, index);
        if (handled) e.Handled = true;

        Diag.Log($"key page    {mods}+{key} -> {command} handled={handled}");
    }

    /// <summary>
    /// Modifier state read from the OS rather than from WPF. Keyboard.Modifiers
    /// tracks WPF's own focus, and during this event focus is inside a window
    /// WPF does not own, so it reports None for every modifier.
    /// </summary>
    private static ModifierKeys NativeModifiers()
    {
        var mods = ModifierKeys.None;
        if (Down(VkControl)) mods |= ModifierKeys.Control;
        if (Down(VkShift)) mods |= ModifierKeys.Shift;
        if (Down(VkMenu)) mods |= ModifierKeys.Alt;
        return mods;

        static bool Down(int vk) => (GetKeyState(vk) & 0x8000) != 0;
    }

    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    // -- The map -------------------------------------------------------------

    /// <summary>
    /// Key plus modifiers to command. Kept static and pure so both paths cannot
    /// drift, and so it is testable without a window.
    ///
    /// The bindings are Chrome's wherever Chrome has one. This is not a place to
    /// be original: these live in muscle memory, and a browser that rebinds
    /// Ctrl+W is not a better browser, it is a broken one.
    /// </summary>
    public static (BrowserCommand Command, int Index) Map(Key key, ModifierKeys mods)
    {
        var ctrl = mods.HasFlag(ModifierKeys.Control);
        var shift = mods.HasFlag(ModifierKeys.Shift);
        var alt = mods.HasFlag(ModifierKeys.Alt);

        // Ctrl+1..8 select by position, Ctrl+9 jumps to the last tab. Chrome's
        // behaviour, including the detail that 9 means "last" and not "ninth".
        if (ctrl && !shift && !alt)
        {
            if (key is >= Key.D1 and <= Key.D8) return (BrowserCommand.SelectTab, key - Key.D1);
            if (key is >= Key.NumPad1 and <= Key.NumPad8)
                return (BrowserCommand.SelectTab, key - Key.NumPad1);
            if (key is Key.D9 or Key.NumPad9) return (BrowserCommand.SelectLastTab, 0);
        }

        return (key, ctrl, shift, alt) switch
        {
            (Key.T, true, false, false) => (BrowserCommand.NewTab, 0),
            (Key.T, true, true, false) => (BrowserCommand.ReopenClosedTab, 0),
            (Key.W, true, false, false) => (BrowserCommand.CloseTab, 0),

            (Key.L, true, false, false) => (BrowserCommand.FocusAddress, 0),
            (Key.D, false, false, true) => (BrowserCommand.FocusAddress, 0),
            (Key.F6, false, false, false) => (BrowserCommand.FocusAddress, 0),

            (Key.R, true, true, false) => (BrowserCommand.HardReload, 0),
            (Key.F5, true, false, false) => (BrowserCommand.HardReload, 0),
            (Key.R, true, false, false) => (BrowserCommand.Reload, 0),
            (Key.F5, false, false, false) => (BrowserCommand.Reload, 0),

            (Key.Left, false, false, true) => (BrowserCommand.Back, 0),
            (Key.Right, false, false, true) => (BrowserCommand.Forward, 0),
            (Key.BrowserBack, _, _, _) => (BrowserCommand.Back, 0),
            (Key.BrowserForward, _, _, _) => (BrowserCommand.Forward, 0),
            (Key.Home, false, false, true) => (BrowserCommand.Home, 0),

            (Key.Tab, true, false, false) => (BrowserCommand.NextTab, 0),
            (Key.Tab, true, true, false) => (BrowserCommand.PreviousTab, 0),
            (Key.PageDown, true, false, false) => (BrowserCommand.NextTab, 0),
            (Key.PageUp, true, false, false) => (BrowserCommand.PreviousTab, 0),

            // OemPlus/OemMinus are the main row; Add/Subtract are the numpad.
            // Chrome accepts both and so does everyone's muscle memory.
            (Key.OemPlus, true, _, false) => (BrowserCommand.ZoomIn, 0),
            (Key.Add, true, _, false) => (BrowserCommand.ZoomIn, 0),
            (Key.OemMinus, true, _, false) => (BrowserCommand.ZoomOut, 0),
            (Key.Subtract, true, _, false) => (BrowserCommand.ZoomOut, 0),
            (Key.D0, true, false, false) => (BrowserCommand.ZoomReset, 0),
            (Key.NumPad0, true, false, false) => (BrowserCommand.ZoomReset, 0),

            // Chrome's "search tabs" chord, pointed at the grid, plus an
            // unmodified key so the grid stays reachable from a lean-back
            // position where the modifier row is not under anyone's hands.
            (Key.A, true, true, false) => (BrowserCommand.ToggleGrid, 0),
            (Key.F9, false, false, false) => (BrowserCommand.ToggleGrid, 0),

            // F11 is the fullscreen key everywhere, and immersion is what
            // fullscreen means here.
            (Key.F11, false, false, false) => (BrowserCommand.ToggleImmersion, 0),

            (Key.Escape, false, false, false) => (BrowserCommand.Dismiss, 0),

            _ => (BrowserCommand.None, 0)
        };
    }

    // -- Reaching the controller ---------------------------------------------

    // Cached so the reflection cost is paid once per process rather than per
    // tab. Resolved lazily: touching WebView2Base before the SDK has loaded
    // would be a worse failure than a late one.
    private static FieldInfo? _baseField;
    private static PropertyInfo? _controllerProperty;
    private static bool _resolved;

    private CoreWebView2Controller? TryGetController(WebView2 view)
    {
        try
        {
            if (!_resolved)
            {
                _baseField = typeof(WebView2).GetField(
                    "m_webview2Base", BindingFlags.Instance | BindingFlags.NonPublic);
                _controllerProperty = _baseField?.FieldType.GetProperty(
                    "CoreWebView2Controller", BindingFlags.Instance | BindingFlags.NonPublic);
                _resolved = true;
            }

            if (_baseField is null || _controllerProperty is null)
            {
                NativeHookError =
                    "WebView2Base.CoreWebView2Controller not found - the pinned SDK may have moved it";
                return null;
            }

            var wrapperBase = _baseField.GetValue(view);
            return wrapperBase is null
                ? null
                : _controllerProperty.GetValue(wrapperBase) as CoreWebView2Controller;
        }
        catch (Exception ex)
        {
            NativeHookError = ex.Message;
            Debug.WriteLine($"[hearth] controller reflection failed: {ex.Message}");
            return null;
        }
    }
}
