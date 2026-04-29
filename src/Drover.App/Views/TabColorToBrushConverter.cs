namespace Drover.App.Views;

/// <summary>
/// One-way converter from a hex string (e.g. "#3D7EFF") on
/// <see cref="Drover.App.Models.ProjectDefinition.TabColor"/> to a frozen
/// <see cref="System.Windows.Media.SolidColorBrush"/> for use in sidebar
/// project dots. Returns <see cref="System.Windows.Media.Brushes.Transparent"/>
/// when the source is null/empty/invalid so the dot reserves space without
/// drawing anything.
/// </summary>
public sealed class TabColorToBrushConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, System.Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!;
                var b = new System.Windows.Media.SolidColorBrush(color);
                b.Freeze();
                return b;
            }
            catch { /* fall through */ }
        }
        return System.Windows.Media.Brushes.Transparent;
    }

    public object ConvertBack(object? value, System.Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => System.Windows.Data.Binding.DoNothing;
}
