using System.Globalization;
using System.Windows.Data;

namespace Drover.App.Views;

/// <summary>
/// Two-way conversion between a fractional scale (1.25) and a percent string
/// ("125%"). Used by the status-bar zoom ComboBox so the user can pick a
/// preset or type a custom value. Tolerates trailing %, leading/trailing
/// whitespace, and invalid input (returns <see cref="Binding.DoNothing"/> on
/// the way back to the source so a half-typed value doesn't blow up).
/// </summary>
public sealed class ScaleToPercentConverter : IValueConverter
{
    public object Convert(object? value, System.Type targetType, object? parameter, CultureInfo culture)
    {
        var scale = value is double d ? d : 1.0;
        var pct = (int)System.Math.Round(scale * 100.0);
        return $"{pct}%";
    }

    public object ConvertBack(object? value, System.Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s) return Binding.DoNothing;
        var trimmed = s.Trim().TrimEnd('%').Trim();
        if (trimmed.Length == 0) return Binding.DoNothing;
        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)
            && !double.TryParse(trimmed, NumberStyles.Float, culture, out pct))
            return Binding.DoNothing;
        var scale = System.Math.Round(pct / 100.0 * 100.0) / 100.0;
        return System.Math.Clamp(scale, 0.7, 2.5);
    }
}
