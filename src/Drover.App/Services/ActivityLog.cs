using System.Collections.ObjectModel;
using System.Windows;
using Drover.App.ViewModels;

namespace Drover.App.Services;

public sealed class ActivityLog
{
    public sealed record Entry(DateTime TimestampUtc, string TabTitle, AttentionState Previous, AttentionState Next)
    {
        public string TimeText => TimestampUtc.ToLocalTime().ToString("HH:mm:ss");
        public string Transition => $"{Previous} → {Next}";
    }

    public ObservableCollection<Entry> Entries { get; } = new();

    public void Record(TerminalTabViewModel tab, AttentionState previous, AttentionState next)
    {
        var entry = new Entry(DateTime.UtcNow, tab.Title, previous, next);
        var app = Application.Current;
        if (app is null) return;
        app.Dispatcher.BeginInvoke(new Action(() =>
        {
            Entries.Insert(0, entry);
            while (Entries.Count > 500) Entries.RemoveAt(Entries.Count - 1);
        }));
    }
}
