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
/// Collapses a git chip when the branch is empty or one of the default names
/// (master / main), even if dirty/ahead/behind marks trail the branch. The git
/// chip format is "branch" or "branch ●↑1↓2" — we split on the first space.
/// </summary>
public sealed class HideDefaultBranchConverter : IValueConverter
{
    public object Convert(object? value, System.Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value as string;
        if (string.IsNullOrEmpty(s)) return System.Windows.Visibility.Collapsed;
        var spaceIdx = s.IndexOf(' ');
        var branch = spaceIdx < 0 ? s : s.Substring(0, spaceIdx);
        if (string.Equals(branch, "master", System.StringComparison.OrdinalIgnoreCase) ||
            string.Equals(branch, "main", System.StringComparison.OrdinalIgnoreCase))
            return System.Windows.Visibility.Collapsed;
        return System.Windows.Visibility.Visible;
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
