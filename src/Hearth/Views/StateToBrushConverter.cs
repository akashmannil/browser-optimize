using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Hearth.Core;

namespace Hearth.Views;

/// <summary>
/// Colours the per-tab state pip. Making renderer cost continuously visible is
/// a product requirement, not decoration — users cannot reason about a resource
/// they never see (p.invisible-cost).
/// </summary>
public sealed class StateToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush LiveBrush       = Freeze("#E8833A"); // holds a renderer
    private static readonly SolidColorBrush WarmBrush       = Freeze("#B7791F");
    private static readonly SolidColorBrush HibernatedBrush = Freeze("#4A7FB5");
    private static readonly SolidColorBrush ColdBrush       = Freeze("#4A4D53"); // costs nothing

    private static SolidColorBrush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is TabState state
            ? state switch
            {
                TabState.Live => LiveBrush,
                TabState.Warm => WarmBrush,
                TabState.Hibernated => HibernatedBrush,
                _ => ColdBrush
            }
            : ColdBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
