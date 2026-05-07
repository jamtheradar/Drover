using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Drover.App.Models;

namespace Drover.App.Views;

/// <summary>
/// Renders a UTC <see cref="System.DateTime"/> as a short relative-time string ("2m
/// ago", "3h ago", "Mar 4"). Returns empty string for null. One-way only.
/// </summary>
public sealed class RelativeTimeConverter : IValueConverter
{
    public object Convert(object? value, System.Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not System.DateTime utc) return string.Empty;
        var local = utc.ToLocalTime();
        var delta = System.DateTime.Now - local;
        if (delta.TotalSeconds < 60) return "just now";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours}h ago";
        if (delta.TotalDays < 7) return $"{(int)delta.TotalDays}d ago";
        return local.ToString("MMM d");
    }

    public object ConvertBack(object? value, System.Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// Pulls the basename out of a <see cref="LocalFileRef.AbsolutePath"/> wrapped in a
/// <see cref="KanbanSourceRef"/>. Empty for non-local sources. One-way only.
/// </summary>
public sealed class SourceRefBasenameConverter : IValueConverter
{
    public object Convert(object? value, System.Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is LocalFileRef lf) return System.IO.Path.GetFileName(lf.AbsolutePath);
        return string.Empty;
    }

    public object ConvertBack(object? value, System.Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
