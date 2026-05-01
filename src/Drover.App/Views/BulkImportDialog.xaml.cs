using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using Drover.App.Models;
using Microsoft.Win32;

namespace Drover.App.Views;

public partial class BulkImportDialog : Window
{
    public sealed class RepoEntry : INotifyPropertyChanged
    {
        public required string Name { get; init; }
        public required string Path { get; init; }
        public required string HintText { get; init; }
        public required bool CanSelect { get; init; }

        private bool _selected;
        public bool Selected
        {
            get => _selected;
            set
            {
                if (_selected == value) return;
                _selected = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public IReadOnlyList<ProjectDefinition> Result { get; private set; } = Array.Empty<ProjectDefinition>();

    private readonly HashSet<string> _existingPaths;
    private readonly ObservableCollection<RepoEntry> _items = new();

    public BulkImportDialog(IEnumerable<ProjectDefinition> existingProjects)
    {
        InitializeComponent();
        _existingPaths = new HashSet<string>(
            existingProjects.Select(p => NormalizePath(p.Path)),
            StringComparer.OrdinalIgnoreCase);
        ResultsList.ItemsSource = _items;
        Loaded += (_, _) => PathBox.Focus();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select parent folder to scan" };
        if (!string.IsNullOrWhiteSpace(PathBox.Text) && Directory.Exists(PathBox.Text))
            dlg.InitialDirectory = PathBox.Text;
        if (dlg.ShowDialog(this) == true)
        {
            PathBox.Text = dlg.FolderName;
            Scan_Click(sender, e);
        }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        var root = PathBox.Text?.Trim();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            StatusText.Text = "pick a folder that exists.";
            _items.Clear();
            return;
        }

        var maxDepth = RecursiveBox.IsChecked == true ? 3 : 1;
        StatusText.Text = "scanning…";
        _items.Clear();

        var hits = await Task.Run(() => ScanForRepos(root, maxDepth));

        foreach (var path in hits.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var name = new DirectoryInfo(path).Name;
            var alreadyAdded = _existingPaths.Contains(NormalizePath(path));
            _items.Add(new RepoEntry
            {
                Name = name,
                Path = path,
                HintText = alreadyAdded ? "already added" : string.Empty,
                CanSelect = !alreadyAdded,
                Selected = !alreadyAdded,
            });
        }

        var newCount = _items.Count(i => i.CanSelect);
        var skipped = _items.Count - newCount;
        StatusText.Text = _items.Count == 0
            ? "no git repositories found."
            : skipped == 0
                ? $"found {_items.Count} repo{(_items.Count == 1 ? "" : "s")}."
                : $"found {_items.Count} ({newCount} new, {skipped} already added).";
    }

    private static List<string> ScanForRepos(string root, int maxDepth)
    {
        var hits = new List<string>();
        Walk(root, 0);
        return hits;

        void Walk(string dir, int depth)
        {
            if (depth >= maxDepth) return;
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(dir);
            }
            catch
            {
                return;
            }

            foreach (var child in children)
            {
                var leaf = System.IO.Path.GetFileName(child);
                // Cheap pruning of folders that obviously won't contain user repos.
                if (leaf is "node_modules" or "bin" or "obj" or ".git" or ".venv" or "__pycache__")
                    continue;

                var gitEntry = System.IO.Path.Combine(child, ".git");
                if (Directory.Exists(gitEntry) || File.Exists(gitEntry))
                {
                    hits.Add(child);
                    // Don't descend into a repo — submodules / worktrees aren't standalone projects.
                    continue;
                }

                Walk(child, depth + 1);
            }
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items)
            if (item.CanSelect) item.Selected = true;
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items) item.Selected = false;
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var picked = _items.Where(i => i.CanSelect && i.Selected).ToList();
        if (picked.Count == 0)
        {
            StatusText.Text = "tick at least one repo to import.";
            return;
        }
        Result = picked
            .Select(i => new ProjectDefinition(i.Name, i.Path, ProjectKind.Claude))
            .ToList();
        DialogResult = true;
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        }
        catch
        {
            return path;
        }
    }
}
