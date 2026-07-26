using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Hearth.Views;

/// <summary>
/// The same motion vocabulary the XAML uses, reachable from code.
///
/// It is duplicated deliberately rather than looked up from the resource
/// dictionary: a typo in a resource key fails silently at runtime and leaves an
/// animation that simply never plays, whereas a typo here does not compile. The
/// values are the ones in App.xaml and the two must be changed together -- there
/// are only four of them, and a mismatch is visible the first time two things
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

    public static readonly IEasingFunction Enter =
        new CubicEase { EasingMode = EasingMode.EaseOut };

    public static readonly IEasingFunction Exit =
        new CubicEase { EasingMode = EasingMode.EaseIn };

    public static readonly IEasingFunction Travel =
        new CubicEase { EasingMode = EasingMode.EaseInOut };

    public static readonly IEasingFunction Emphasis =
        new QuinticEase { EasingMode = EasingMode.EaseOut };

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
        var animation = new DoubleAnimation(value, duration) { EasingFunction = easing ?? Enter };
        if (beginAt is { } delay) animation.BeginTime = delay;
        return animation;
    }

    public static DoubleAnimation From(
        double from, double to, Duration duration, IEasingFunction? easing = null,
        TimeSpan? beginAt = null)
    {
        var animation = new DoubleAnimation(from, to, duration) { EasingFunction = easing ?? Enter };
        if (beginAt is { } delay) animation.BeginTime = delay;
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
}
