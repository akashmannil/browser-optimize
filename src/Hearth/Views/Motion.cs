using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Hearth.Core;

namespace Hearth.Views;

/// <summary>
/// The same motion vocabulary the XAML uses, reachable from code.
///
/// It is duplicated deliberately rather than looked up from the resource
/// dictionary: a typo in a resource key fails silently at runtime and leaves an
/// animation that simply never plays, whereas a typo here does not compile. The
/// values are the ones in App.xaml and the two must be changed together -- there
/// are only five of them, and a mismatch is visible the first time two things
/// move next to each other.
///
/// PERFORMANCE RULE (d.animate-transforms-only): everything here targets Opacity
/// or a Transform. Both are composited from a retained visual, so a longer
/// animation costs no more layout work than a short one -- which is what makes
/// it safe to slow the whole app down in the name of smoothness.
/// </summary>
internal static class Motion
{
    public static readonly Duration Quick = new(TimeSpan.FromMilliseconds(130));
    public static readonly Duration Base = new(TimeSpan.FromMilliseconds(260));
    public static readonly Duration Slow = new(TimeSpan.FromMilliseconds(440));
    public static readonly Duration Grand = new(TimeSpan.FromMilliseconds(580));

    /// <summary>
    /// The whole-screen moves: the startup reveal, and handing one process over
    /// to another across a mode restart. Long enough to read as a scene change
    /// rather than a redraw, and used by almost nothing, which is the point.
    /// </summary>
    public static readonly Duration Scene = new(TimeSpan.FromMilliseconds(820));

    public static readonly IEasingFunction Enter =
        new CubicEase { EasingMode = EasingMode.EaseOut };

    public static readonly IEasingFunction Exit =
        new CubicEase { EasingMode = EasingMode.EaseIn };

    public static readonly IEasingFunction Travel =
        new CubicEase { EasingMode = EasingMode.EaseInOut };

    public static readonly IEasingFunction Emphasis =
        new QuinticEase { EasingMode = EasingMode.EaseOut };

    /// <summary>
    /// Whether the long transitions play at all.
    ///
    /// Windows has a system-wide "show animations" switch, and a browser is
    /// exactly the kind of application people turn it off FOR -- it is set by
    /// users on weak hardware, over remote desktop, and by anyone whose balance
    /// disorder makes a full-screen zoom genuinely unpleasant. Every other
    /// browser honours it. Reading it here rather than at each call site means a
    /// transition added later cannot forget to.
    ///
    /// This deliberately does NOT cover pointer feedback: a hover fill and a
    /// press dip are 70-130 ms, carry no travel, and removing them makes the
    /// controls feel broken rather than calm. What it covers is everything that
    /// MOVES something across the screen.
    /// </summary>
    public static readonly bool Enabled = ReadEnabled();

    private static bool ReadEnabled()
    {
        // An explicit override wins, so a benchmark run can pin it either way
        // without touching the machine's accessibility settings.
        if (Environment.GetEnvironmentVariable("HEARTH_MOTION") is { } pinned)
            return pinned is not ("0" or "off" or "false");

        try
        {
            return SystemParameters.ClientAreaAnimation;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// A duration as it should actually be played. Collapses to a single tick
    /// when motion is off, so an animation still fires -- and still raises
    /// Completed, which the transition sequencing depends on -- but lands
    /// immediately.
    /// </summary>
    public static Duration Play(Duration duration) =>
        Enabled ? duration : new Duration(TimeSpan.FromMilliseconds(1));

    /// <summary>A wait matched to a played duration, for sequencing.</summary>
    public static TimeSpan Wait(Duration duration) => Play(duration).TimeSpan;

    // BeginTime is assigned ONLY when a delay was actually asked for.
    //
    // Timeline.BeginTime is a TimeSpan? whose default is TimeSpan.Zero, and
    // setting it to null does not mean "no delay" -- it means the timeline never
    // begins. Passing a nullable straight through therefore disables every
    // animation that did not request a stagger, silently and with no error: the
    // property simply keeps its declared value. It cost one build to find,
    // because the tab grid opened completely empty (its Opacity is declared 0 in
    // XAML and the animation that raises it never ran).
    public static DoubleAnimation To(
        double value, Duration duration, IEasingFunction? easing = null,
        TimeSpan? beginAt = null)
    {
        var animation = new DoubleAnimation(value, Play(duration))
        {
            EasingFunction = easing ?? Enter
        };
        if (beginAt is { } delay && Enabled) animation.BeginTime = delay;
        return animation;
    }

    public static DoubleAnimation From(
        double from, double to, Duration duration, IEasingFunction? easing = null,
        TimeSpan? beginAt = null)
    {
        var animation = new DoubleAnimation(from, to, Play(duration))
        {
            EasingFunction = easing ?? Enter
        };
        if (beginAt is { } delay && Enabled) animation.BeginTime = delay;
        return animation;
    }

    /// <summary>
    /// Fades an element and completes when the animation does. Used where the
    /// next step must not start early -- a Task.Delay guess that runs short
    /// produces exactly the visible seam these transitions exist to remove.
    /// </summary>
    public static Task FadeAsync(
        UIElement element, double to, Duration duration, IEasingFunction? easing = null)
    {
        var completion = new TaskCompletionSource();
        var animation = To(to, duration, easing);

        animation.Completed += (_, _) => completion.TrySetResult();
        element.BeginAnimation(UIElement.OpacityProperty, animation);

        return completion.Task;
    }

    /// <summary>
    /// Runs an animation to completion on any animatable property, and awaits
    /// it. The Completed event does not fire for a Duration of zero, so the
    /// motion-off path is short-circuited rather than left to hang.
    /// </summary>
    public static Task RunAsync(
        IAnimatable target, DependencyProperty property, DoubleAnimation animation)
    {
        var completion = new TaskCompletionSource();
        animation.Completed += (_, _) => completion.TrySetResult();
        target.BeginAnimation(property, animation);
        return completion.Task;
    }

    /// <summary>
    /// Gives an element a fresh translate + scale pair to animate, returning
    /// both. Every arrival in the app is the same gesture -- come up a little
    /// and grow a little -- and hand-building a TransformGroup at each call site
    /// is how they drifted apart before 0013.
    /// </summary>
    public static (TranslateTransform Slide, ScaleTransform Grow) Rig(
        FrameworkElement element, double fromY = 0, double fromScale = 1)
    {
        var slide = new TranslateTransform(0, fromY);
        var grow = new ScaleTransform(fromScale, fromScale);

        var group = new TransformGroup();
        group.Children.Add(grow);
        group.Children.Add(slide);

        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = group;

        return (slide, grow);
    }

    /// <summary>Clears animations and transforms an element was rigged with.</summary>
    public static void Unrig(FrameworkElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1;
        element.RenderTransform = Transform.Identity;
    }

    static Motion()
    {
        Diag.Log($"motion: enabled={Enabled}");
    }
}
