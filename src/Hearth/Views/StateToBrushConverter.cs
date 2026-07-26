using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Hearth.Core;

namespace Hearth.Views;

/// <summary>
/// Colours the per-tab heat pip. Making renderer cost continuously visible is a
/// product requirement, not decoration -- users cannot reason about a resource
/// they never see (p.invisible-cost).
///
/// Brushes are resolved from the live theme dictionary on every call rather than
/// cached, so a theme swap repaints the pips along with everything else. A
/// converter that caches brushes is the classic way a theme switch ends up
/// half-applied.
/// </summary>
public sealed class StateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is TabState state
            ? state switch
            {
                TabState.Live => "StateLive",
                TabState.Warm => "StateWarm",
                TabState.Hibernated => "StateHibernated",
                _ => "StateCold"
            }
            : "StateCold";

        return Application.Current?.TryFindResource(key) as Brush
               ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
