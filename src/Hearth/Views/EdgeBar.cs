using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Hearth.Views;

/// <summary>
/// A strip of chrome that slides in at the top or bottom edge of the window in
/// immersion mode, and is a SEPARATE TOP-LEVEL WINDOW rather than an overlay.
///
/// WHY A WHOLE WINDOW FOR A TOOLBAR (k.wpf-airspace, again). WebView2 is a
/// windowed HWND child and paints above every piece of WPF content regardless of
/// Z-order. An immersion toolbar drawn inside MainWindow would therefore be
/// invisible the moment a page painted -- the same wall commit 0006 hit with the
/// tab grid. There the answer was to collapse the content host, which is
/// available to a full-screen surface and useless to a toolbar: the entire point
/// is to show controls WITHOUT hiding the page.
///
/// The alternative was to let the rows reflow, growing the window's layout to
/// make room. That resizes the WebView2 on every reveal, which reflows the page
/// underneath -- unacceptable for the mode built around watching video.
///
/// A separate owned window sidesteps both. It is a sibling HWND rather than a
/// child, so it composites above the WebView2, and the page below it never
/// changes size.
/// </summary>
public sealed class EdgeBar : Window
{
    public enum Side { Top, Bottom }

    private readonly Side _side;
    private readonly FrameworkElement _panel;
    private readonly TranslateTransform _slide = new();
    private bool _revealed;

    public EdgeBar(Window owner, Side side, FrameworkElement panel, double height)
    {
        _side = side;
        _panel = panel;

        Owner = owner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        Height = height;

        // Never steal focus. Revealing a toolbar must not pull the caret out of
        // whatever the page was doing, and an activating window would also drop
        // the owner out of the shell's fullscreen treatment and bring the
        // taskbar back (k.maximised-is-not-fullscreen).
        ShowActivated = false;

        panel.RenderTransform = _slide;
        panel.Opacity = 0;
        Content = panel;

        // Keep the bar up while the pointer is on it. Once the pointer is over
        // this window the page stops receiving mousemove, so the page-side edge
        // detector cannot report anything either way.
        MouseEnter += (_, _) => Reveal();
        MouseLeave += (_, _) => Conceal();
    }

    public bool IsRevealed => _revealed;

    public void SyncBounds()
    {
        if (Owner is not { } owner) return;

        Left = owner.Left;
        Width = owner.ActualWidth;
        Top = _side == Side.Top
            ? owner.Top
            : owner.Top + owner.ActualHeight - Height;
    }

    public void Reveal()
    {
        if (_revealed) return;
        _revealed = true;

        SyncBounds();
        if (!IsVisible) Show();

        Animate(to: 0, opacity: 1, milliseconds: 220);
    }

    public void Conceal()
    {
        if (!_revealed) return;
        _revealed = false;

        Animate(to: _side == Side.Top ? -Height : Height, opacity: 0, milliseconds: 170);
    }

    /// <summary>
    /// Slides the panel rather than the window. Moving the window itself would
    /// make Windows redraw the region underneath on every frame, and the region
    /// underneath is a live page.
    /// </summary>
    private void Animate(double to, double opacity, int milliseconds)
    {
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        if (_revealed) _slide.Y = _side == Side.Top ? -Height : Height;

        _slide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(to, duration) { EasingFunction = ease });

        _panel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(opacity, duration) { EasingFunction = ease });
    }

    /// <summary>
    /// Returns the hosted panel to a normal parent and closes. Used when the
    /// window is going away, so the panel is not left owned by a dead window.
    /// </summary>
    public FrameworkElement Release()
    {
        Content = null;
        _panel.RenderTransform = null;
        _panel.Opacity = 1;
        Close();
        return _panel;
    }
}
