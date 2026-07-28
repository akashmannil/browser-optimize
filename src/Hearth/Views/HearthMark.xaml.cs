using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace Hearth.Views;

/// <summary>
/// The Hearth mark, as vectors. See HearthMark.xaml for why it is not the icon
/// bitmap.
/// </summary>
public partial class HearthMark : UserControl
{
    public HearthMark() => InitializeComponent();

    /// <summary>
    /// Banks the ember: a slow, shallow breath on the halo only.
    ///
    /// This runs while the browser is starting, which is the busiest moment in
    /// the process, so it is deliberately the cheapest possible animation --
    /// one Opacity on one small leaf, no effect, no layout, no transform. The
    /// point is to say the application is alive rather than hung; a spinner
    /// would say the same thing and would be a second moving element competing
    /// with the shell coming up.
    /// </summary>
    public void Breathe()
    {
        if (!Motion.Enabled) return;

        Halo.BeginAnimation(OpacityProperty, new DoubleAnimation(0.12, 1.0,
            new System.Windows.Duration(TimeSpan.FromMilliseconds(1500)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = Motion.Travel
        });
    }

    public void Settle() => Halo.BeginAnimation(OpacityProperty, null);
}
