namespace Drover.App.Views;

/// <summary>
/// Two-way converter between a `double` (pixels) on the VM and a `GridLength`
/// on a ColumnDefinition.Width. Used so the right-pane GridSplitter can drag
/// the column directly while the VM owns the persisted width.
/// </summary>
public sealed class DoubleToGridLengthConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, System.Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is double d) return new System.Windows.GridLength(d, System.Windows.GridUnitType.Pixel);
        return new System.Windows.GridLength(0);
    }

    public object ConvertBack(object? value, System.Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is System.Windows.GridLength gl && gl.IsAbsolute) return gl.Value;
        return 0d;
    }
}
