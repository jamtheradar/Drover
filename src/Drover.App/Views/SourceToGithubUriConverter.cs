using System;
using System.Globalization;
using System.Windows.Data;

namespace Drover.App.Views;

// Maps a SkillSummary.Source ("owner/repo" for github sources, "host" for
// well-known) to a browseable URL. github gets the standard repo URL;
// anything else gets `https://<source>` so well-known hosts (e.g.
// `mintlify.com`) resolve to a real page.
public sealed class SourceToGithubUriConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrWhiteSpace(s)) return null;
        if (s.Contains('/')) return new Uri($"https://github.com/{s}", UriKind.Absolute);
        return new Uri($"https://{s}", UriKind.Absolute);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
