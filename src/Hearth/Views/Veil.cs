using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Hearth.Views;

/// <summary>
/// A full-window curtain carrying the Hearth mark. It covers the browser while
/// it is starting, and again while one process hands over to the next across a
/// mode restart.
///
/// WHY A SEPARATE WINDOW, AGAIN (k.wpf-airspace). An overlay inside MainWindow
/// cannot cover the page: WebView2 is a windowed HWND child and paints above all
/// WPF content regardless of Z-order. The startup curtain would therefore be
/// punched through by the first page the moment it painted -- in the middle of
/// the screen, which is exactly where the mark is. The tab grid solves the same
/// problem by collapsing the content host, which is not available here: a
/// WebView2 cannot finish initialising inside a collapsed panel
/// (k.collapsed-host-blocks-initialisation), and initialising is precisely what
/// the curtain is covering. An owned top-level window is a sibling HWND, so it
/// composites above the page and needs nothing hidden
/// (d.airspace-needs-a-second-window).
///
/// TWO WAYS TO LEAVE, and the difference is measured rather than assumed --
/// see <see cref="Dissolves"/>.
/// </summary>
public sealed class Veil : Window
{
    private readonly Window _owner;
    private readonly HearthMark _mark;
    private readonly StackPanel _content;

    /// <summary>
    /// Whether the curtain fades the WHOLE WINDOW out to reveal the browser
    /// underneath, or only fades its own contents and then vanishes.
    ///
    /// Window opacity is the better-looking of the two by a wide margin: the
    /// chrome and the live page dissolve into view together, which is what
    /// "fade in to the browser" actually means. It also requires
    /// AllowsTransparency, and a per-pixel-alpha window in WPF is composited in
    /// SOFTWARE -- the entire window, every frame, on the UI thread. At the size
    /// of a browser that is not a detail (k.layered-windows-are-software-drawn).
    ///
    /// Opaque instead fades only the mark, over a ground the same colour as the
    /// app's, then closes. The seam is a single frame where the page appears at
    /// full strength; the chrome fade that follows covers it.
    ///
    /// HEARTH_VEIL=dissolve pins the transparent path so the two can be measured
    /// against each other on one build.
    /// </summary>
    public static bool Dissolves { get; } =
        Environment.GetEnvironmentVariable("HEARTH_VEIL") is "dissolve";

    private Veil(Window owner, string? caption)
    {
        _owner = owner;

        Owner = owner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;

        // Never take focus. The shell puts the caret in the address bar while
        // this is up, and an activating window would steal it back -- and would
        // drop an immersion window out of the shell's fullscreen treatment,
        // bringing the taskbar over the top of the curtain
        // (k.maximised-is-not-fullscreen).
        ShowActivated = false;

        AllowsTransparency = Dissolves;
        Background = (Brush)Application.Current.FindResource("Bg");

        _mark = new HearthMark { Width = 118, Height = 118 };

        _content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0
        };
        _content.Children.Add(_mark);
        _content.Children.Add(new TextBlock
        {
            Text = "Hearth",
            Margin = new Thickness(0, 22, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            FontFamily = (FontFamily)Application.Current.FindResource("Display"),
            FontSize = 26,
            FontWeight = FontWeights.Light,
            Foreground = (Brush)Application.Current.FindResource("TextPrimary")
        });

        if (caption is not null)
        {
            _content.Children.Add(new TextBlock
            {
                Text = caption,
                Margin = new Thickness(0, 10, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                FontFamily = (FontFamily)Application.Current.FindResource("Body"),
                FontSize = (double)Application.Current.FindResource("TextSM"),
                Foreground = (Brush)Application.Current.FindResource("TextTertiary")
            });
        }

        Content = new Grid { Children = { _content } };

        SyncBounds();
        owner.LocationChanged += OnOwnerMoved;
        owner.SizeChanged += OnOwnerResized;
    }

    private void OnOwnerMoved(object? sender, EventArgs e) => SyncBounds();

    private void OnOwnerResized(object? sender, SizeChangedEventArgs e) => SyncBounds();

    private void SyncBounds()
    {
        if (_owner.WindowState == WindowState.Minimized) return;

        // Deliberately the owner's OUTER bounds rather than its client area.
        // Hearth draws its own caption (there is no native title bar), so the
        // window's full rectangle is content and any strip left uncovered would
        // be a bright line along an edge of the curtain.
        Left = _owner.Left;
        Top = _owner.Top;
        Width = Math.Max(_owner.ActualWidth, 1);
        Height = Math.Max(_owner.ActualHeight, 1);
    }

    /// <summary>Raises the curtain and brings the mark up under it.</summary>
    public static Veil Cover(Window owner, string? caption = null)
    {
        var veil = new Veil(owner, caption);
        veil.Show();

        // The mark arrives rather than appearing: up a little, out a little.
        // Same gesture as every other arrival in the app (Motion.Rig).
        var (slide, grow) = Motion.Rig(veil._content, fromY: 14, fromScale: 0.94);

        veil._content.BeginAnimation(OpacityProperty, Motion.From(0, 1, Motion.Slow));
        slide.BeginAnimation(TranslateTransform.YProperty,
            Motion.From(14, 0, Motion.Slow, Motion.Emphasis));
        grow.BeginAnimation(ScaleTransform.ScaleXProperty,
            Motion.From(0.94, 1, Motion.Slow, Motion.Emphasis));
        grow.BeginAnimation(ScaleTransform.ScaleYProperty,
            Motion.From(0.94, 1, Motion.Slow, Motion.Emphasis));

        veil._mark.Breathe();
        return veil;
    }

    /// <summary>
    /// Lowers the curtain. Completes only once nothing of it is left on screen,
    /// so the caller can start the next beat without overlapping this one.
    /// </summary>
    public async Task DismissAsync()
    {
        _mark.Settle();

        using (FrameMeter.Start(Dissolves ? "veil-dissolve" : "veil-opaque"))
        {
            if (Dissolves)
            {
                // Everything goes at once: mark, ground, and the window itself.
                // The browser is revealed BY the fade rather than after it.
                _content.BeginAnimation(OpacityProperty, Motion.To(0, Motion.Base, Motion.Exit));
                await Motion.RunAsync(this, OpacityProperty,
                    Motion.To(0, Motion.Scene, Motion.Travel));
            }
            else
            {
                // The mark lifts away as it fades -- it is being replaced by the
                // thing it was standing in for, so it leaves upward and slightly
                // larger, the way the grid's card does when it becomes a page.
                if (_content.RenderTransform is TransformGroup
                    { Children: [ScaleTransform grow, TranslateTransform slide] })
                {
                    slide.BeginAnimation(TranslateTransform.YProperty,
                        Motion.To(-10, Motion.Slow, Motion.Exit));
                    grow.BeginAnimation(ScaleTransform.ScaleXProperty,
                        Motion.To(1.06, Motion.Slow, Motion.Exit));
                    grow.BeginAnimation(ScaleTransform.ScaleYProperty,
                        Motion.To(1.06, Motion.Slow, Motion.Exit));
                }

                await Motion.FadeAsync(_content, 0, Motion.Slow, Motion.Exit);
            }
        }

        Release();
    }

    /// <summary>
    /// Closes without animating. Used when the process is going away anyway --
    /// there is nothing left to reveal, and a fade would only delay the exit.
    /// </summary>
    public void Release()
    {
        _owner.LocationChanged -= OnOwnerMoved;
        _owner.SizeChanged -= OnOwnerResized;
        Close();
    }
}
