namespace Drover.App.Views;

/// <summary>
/// Multiplies a base font size (passed as ConverterParameter, default 13)
/// by a scale value (the bound source). Used by the plan panel so all its
/// text sizes track <c>ShellViewModel.PlanFontScale</c> together.
/// </summary>
public sealed class ScaleFontSizeConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, System.Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        double scale = value is double d ? d : 1.0;
        double basePx = 13.0;
        if (parameter is string s && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            basePx = parsed;
        else if (parameter is double dd) basePx = dd;
        return basePx * scale;
    }

    public object ConvertBack(object? value, System.Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => System.Windows.DependencyProperty.UnsetValue;
}
