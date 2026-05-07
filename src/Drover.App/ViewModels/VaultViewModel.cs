using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Drover.App.Services;
using Drover.Vault.Core;
using Drover.Vault.Core.Models;

namespace Drover.App.ViewModels;

// Backing VM for the right-pane VaultPanel. Singleton (lives on
// ShellViewModel.Vault) so the editor buffer and dirty flag survive
// panel toggles and tab switches; OnActiveProjectChanged re-targets
// it when the selected tab moves to a different project. Subscribes
// to VaultStore.Changed so external edits re-list the tree (and
// reload the open file when the editor is clean).
//
// External-edit handling: when Changed fires for the currently-open file
// and IsDirty is false, the editor reloads silently; when dirty, the
// view shows an ExternalEditPending strip and the user picks Reload or
// Keep via ReloadExternalCommand / DismissExternalCommand.
public sealed partial class VaultViewModel : ObservableObject
{
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(250);

    private readonly VaultStore _store;
    private readonly DispatcherTimer _searchDebounceTimer;
    private bool _suppressDirty;

    public ObservableCollection<VaultTreeNode> Entries { get; } = new();

    [ObservableProperty] private VaultScope _scope = VaultScope.Project;
    [ObservableProperty] private VaultTreeNode? _selectedEntry;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorEnabled))]
    private string _editorText = string.Empty;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isDirty;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _projectScopeAvailable;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _externalEditPending;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _isSearching;

    public bool EditorEnabled => SelectedEntry is { IsFolder: false };

    public VaultViewModel(VaultStore store)
    {
        _store = store;
        ProjectScopeAvailable = !string.IsNullOrEmpty(_store.ActiveProjectPath);
        _store.Changed += OnVaultStoreChanged;

        _searchDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = SearchDebounce,
        };
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            RebuildTree();
        };

        // Default scope: prefer Project when available so the user lands on
        // their working namespace; fall back to Global if no project active.
        Scope = ProjectScopeAvailable ? VaultScope.Project : VaultScope.Global;
        RebuildTree();
    }

    public void Detach()
    {
        _store.Changed -= OnVaultStoreChanged;
        _searchDebounceTimer.Stop();
    }

    /// <summary>
    /// Re-target the VM after <see cref="VaultStore.SetActiveProject"/>
    /// has been called for a different project. Updates
    /// <see cref="ProjectScopeAvailable"/>, clears the editor, and
    /// rebuilds the tree against the new namespace. If the user was
    /// in Project scope and the new project is unavailable, falls back
    /// to Global so the panel never lands in an empty unbound state.
    /// </summary>
    public void OnActiveProjectChanged()
    {
        var available = !string.IsNullOrEmpty(_store.ActiveProjectPath);
        ProjectScopeAvailable = available;
        SelectedEntry = null;
        ExternalEditPending = false;
        ResetEditor(string.Empty);
        if (Scope == VaultScope.Project && !available)
        {
            // Side-effect: OnScopeChanged rebuilds the tree.
            Scope = VaultScope.Global;
        }
        else
        {
            RebuildTree();
        }
    }

    partial void OnScopeChanged(VaultScope value)
    {
        SelectedEntry = null;
        ExternalEditPending = false;
        ResetEditor(string.Empty);
        RebuildTree();
    }

    partial void OnSearchTextChanged(string value)
    {
        // Debounce both directions — clearing the box should also wait one
        // tick so a fast backspace storm doesn't thrash the tree.
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    partial void OnSelectedEntryChanged(VaultTreeNode? value)
    {
        OnPropertyChanged(nameof(EditorEnabled));
        ExternalEditPending = false;
        if (value is null || value.IsFolder)
        {
            ResetEditor(string.Empty);
            return;
        }
        LoadSelectedFromDisk();
    }

    partial void OnEditorTextChanged(string value)
    {
        if (_suppressDirty) return;
        IsDirty = true;
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (SelectedEntry is null || SelectedEntry.IsFolder) return;
        try
        {
            _store.Write(Scope, SelectedEntry.RelativePath, EditorText);
            IsDirty = false;
            ExternalEditPending = false;
            StatusText = $"Saved {SelectedEntry.RelativePath}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Save failed: {ex.Message}";
        }
    }

    private bool CanSave() => IsDirty && SelectedEntry is { IsFolder: false };

    [RelayCommand]
    private void Refresh() => RebuildTree();

    public bool TryCreateNote(string relativePath, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            error = "Path must be non-empty.";
            return false;
        }

        var normalized = relativePath.Trim().Replace('\\', '/');
        if (!normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            normalized += ".md";

        try
        {
            // Existence check + write happen against VaultStore; ResolveSafe
            // throws VaultPathException if the relative path escapes the
            // namespace root.
            _store.Write(Scope, normalized, string.Empty);
        }
        catch (VaultPathException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        StatusText = $"Created {normalized}.";
        RebuildTree();
        // Auto-select the new entry.
        var leaf = FindLeaf(Entries, normalized);
        if (leaf is not null)
        {
            ExpandAncestors(leaf);
            leaf.IsSelected = true;
            SelectedEntry = leaf;
        }
        return true;
    }

    [RelayCommand]
    private void ReloadExternal()
    {
        if (SelectedEntry is null || SelectedEntry.IsFolder) return;
        LoadSelectedFromDisk();
        ExternalEditPending = false;
    }

    [RelayCommand]
    private void DismissExternal() => ExternalEditPending = false;

    public static string DefaultNewNoteSuggestion()
        => $"notes/{DateTime.Now:yyyy-MM-dd}.md";

    private void LoadSelectedFromDisk()
    {
        if (SelectedEntry is null || SelectedEntry.IsFolder) return;
        try
        {
            var text = _store.Read(Scope, SelectedEntry.RelativePath);
            ResetEditor(text);
            StatusText = $"Loaded {SelectedEntry.RelativePath}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Load failed: {ex.Message}";
            ResetEditor(string.Empty);
        }
    }

    private void ResetEditor(string text)
    {
        _suppressDirty = true;
        try
        {
            EditorText = text;
            IsDirty = false;
        }
        finally { _suppressDirty = false; }
    }

    private void OnVaultStoreChanged(object? sender, EventArgs e)
    {
        // External-edit branch: if the currently-open file is clean,
        // reload silently. If dirty, surface the conflict strip and let
        // the user choose Reload or Keep.
        if (SelectedEntry is { IsFolder: false } sel)
        {
            if (!IsDirty)
            {
                LoadSelectedFromDisk();
            }
            else
            {
                ExternalEditPending = true;
            }
        }
        RebuildTree();
    }

    private bool ScopeReady() => Scope == VaultScope.Global || ProjectScopeAvailable;

    private void RebuildTree()
    {
        // Preserve selection target across rebuilds — the user shouldn't
        // lose their place because someone touched a different file.
        var previousPath = SelectedEntry?.RelativePath;
        Entries.Clear();
        IsEmpty = false;
        IsSearching = !string.IsNullOrWhiteSpace(SearchText);

        if (!ScopeReady())
        {
            IsEmpty = true;
            return;
        }

        try
        {
            if (IsSearching)
            {
                BuildFlatSearchResults(SearchText.Trim());
            }
            else
            {
                BuildTree();
            }
        }
        catch (Exception ex)
        {
            StatusText = $"List failed: {ex.Message}";
            IsEmpty = true;
            return;
        }

        IsEmpty = Entries.Count == 0;
        if (previousPath is not null)
        {
            var leaf = FindLeaf(Entries, previousPath);
            if (leaf is not null)
            {
                ExpandAncestors(leaf);
                leaf.IsSelected = true;
            }
        }
    }

    private void BuildTree()
    {
        var entries = _store.List(Scope);
        var rootChildren = new Dictionary<string, VaultTreeNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var parts = entry.RelativePath.Split('/');
            if (parts.Length == 0) continue;

            ICollection<VaultTreeNode> currentLevel = Entries;
            Dictionary<string, VaultTreeNode> currentMap = rootChildren;
            string currentPath = string.Empty;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                var segment = parts[i];
                currentPath = currentPath.Length == 0 ? segment : $"{currentPath}/{segment}";
                if (!currentMap.TryGetValue(segment, out var folder))
                {
                    folder = new VaultTreeNode(segment, currentPath, isFolder: true, summary: null);
                    currentMap[segment] = folder;
                    currentLevel.Add(folder);
                }
                currentLevel = folder.Children;
                currentMap = folder.ChildrenMap;
            }

            var leafName = parts[^1];
            var leaf = new VaultTreeNode(leafName, entry.RelativePath, isFolder: false, summary: entry.Summary);
            currentLevel.Add(leaf);
        }

        SortInPlace(Entries);
    }

    private void BuildFlatSearchResults(string query)
    {
        // Filename-only filter v1, per spec out-of-scope notes. List then
        // substring-match on RelativePath. Snippet display is deferred.
        var entries = _store.List(Scope);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (e.RelativePath.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (!seen.Add(e.RelativePath)) continue;
            Entries.Add(new VaultTreeNode(e.RelativePath, e.RelativePath, isFolder: false, summary: e.Summary));
        }
    }

    private static void SortInPlace(ObservableCollection<VaultTreeNode> nodes)
    {
        var sorted = nodes
            .OrderByDescending(n => n.IsFolder)
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        nodes.Clear();
        foreach (var n in sorted)
        {
            if (n.IsFolder) SortInPlace(n.Children);
            nodes.Add(n);
        }
    }

    private static VaultTreeNode? FindLeaf(IEnumerable<VaultTreeNode> nodes, string relativePath)
    {
        foreach (var n in nodes)
        {
            if (!n.IsFolder && string.Equals(n.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
                return n;
            if (n.IsFolder)
            {
                var hit = FindLeaf(n.Children, relativePath);
                if (hit is not null) return hit;
            }
        }
        return null;
    }

    private void ExpandAncestors(VaultTreeNode leaf)
    {
        // Walk the relative path back through Entries and expand each folder
        // node along the way. Cheap for trees this size; no parent pointer
        // needed.
        if (string.IsNullOrEmpty(leaf.RelativePath)) return;
        var parts = leaf.RelativePath.Split('/');
        if (parts.Length <= 1) return;

        ICollection<VaultTreeNode> level = Entries;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            var segment = parts[i];
            var folder = level.FirstOrDefault(n => n.IsFolder && string.Equals(n.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (folder is null) return;
            folder.IsExpanded = true;
            level = folder.Children;
        }
    }
}

public sealed partial class VaultTreeNode : ObservableObject
{
    public string Name { get; }
    public string RelativePath { get; }
    public bool IsFolder { get; }
    public string? Summary { get; }

    public ObservableCollection<VaultTreeNode> Children { get; } = new();

    // Only populated for folders during tree construction so the builder
    // can de-dup folder entries by name without an O(n) scan per part.
    internal Dictionary<string, VaultTreeNode> ChildrenMap { get; }

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    public VaultTreeNode(string name, string relativePath, bool isFolder, string? summary)
    {
        Name = name;
        RelativePath = relativePath;
        IsFolder = isFolder;
        Summary = summary;
        ChildrenMap = isFolder ? new Dictionary<string, VaultTreeNode>(StringComparer.OrdinalIgnoreCase) : null!;
    }
}
