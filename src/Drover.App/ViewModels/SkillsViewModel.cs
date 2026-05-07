using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Drover.App.Models;
using Drover.App.Services;

namespace Drover.App.ViewModels;

// Backing VM for SkillsWindow. Single flat catalog over the local skills-index
// produced by drover-skills-indexer. Search and category are pure in-memory
// filters via a CollectionView — no network calls, no pagination.
public sealed partial class SkillsViewModel : ObservableObject
{
    public enum LoadState { Idle, Loading, Empty, Error }

    public const string AllCategoriesLabel = "All";

    private readonly SkillsRepoCatalog _repoCatalog;
    private readonly SettingsStore _settingsStore;
    private CancellationTokenSource? _loadCts;
    private readonly Dictionary<string, CommunitySkill> _skillByDedupKey = new(StringComparer.OrdinalIgnoreCase);

    public ProjectDefinition Project { get; }

    public ObservableCollection<SkillRow> Skills { get; } = new();
    public ICollectionView SkillsView { get; }
    public ObservableCollection<string> Categories { get; } = new();

    [ObservableProperty] private LoadState _state = LoadState.Idle;
    [ObservableProperty] private string _errorText = string.Empty;
    [ObservableProperty] private SkillRow? _selectedSkill;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _selectedCategory = AllCategoriesLabel;
    [ObservableProperty] private bool _installGlobally;
    [ObservableProperty] private string _statusText = string.Empty;

    public event EventHandler<string>? RequestInstall;

    public SkillsViewModel(SkillsRepoCatalog repoCatalog, SettingsStore settingsStore, ProjectDefinition project)
    {
        _repoCatalog = repoCatalog;
        _settingsStore = settingsStore;
        Project = project;
        SkillsView = CollectionViewSource.GetDefaultView(Skills);
        SkillsView.Filter = FilterPredicate;
        SkillsView.SortDescriptions.Add(new SortDescription(nameof(SkillRow.Name), ListSortDirection.Ascending));
        Categories.Add(AllCategoriesLabel);
    }

    partial void OnSearchTextChanged(string value) => SkillsView.Refresh();
    partial void OnSelectedCategoryChanged(string value) => SkillsView.Refresh();

    private bool FilterPredicate(object item)
    {
        if (item is not SkillRow row) return false;
        if (!string.IsNullOrEmpty(SelectedCategory) &&
            !string.Equals(SelectedCategory, AllCategoriesLabel, StringComparison.Ordinal) &&
            !string.Equals(row.Category, SelectedCategory, StringComparison.Ordinal))
            return false;

        var q = SearchText?.Trim();
        if (string.IsNullOrEmpty(q)) return true;
        return ContainsCi(row.Name, q)
            || ContainsCi(row.Description, q)
            || ContainsCi(row.Source, q)
            || ContainsCi(row.Slug, q)
            || ContainsCi(row.Category, q);

        static bool ContainsCi(string? haystack, string needle)
            => !string.IsNullOrEmpty(haystack) && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    public async Task LoadAsync() => await LoadInternalAsync(forceRefresh: false);

    [RelayCommand]
    public async Task RefreshAsync() => await LoadInternalAsync(forceRefresh: true);

    [RelayCommand]
    public Task InstallAsync()
    {
        var row = SelectedSkill;
        if (row is null) return Task.CompletedTask;
        if (!_skillByDedupKey.TryGetValue(row.Slug, out var community))
        {
            StatusText = "Install failed: skill not in local index.";
            return Task.CompletedTask;
        }
        return InstallCommunityAsync(community);
    }

    private async Task InstallCommunityAsync(CommunitySkill community)
    {
        try
        {
            StatusText = $"Installing {community.Slug}…";
            var dest = await _repoCatalog.InstallAsync(community, InstallGlobally);
            StatusText = $"Installed to {dest}";
        }
        catch (Exception ex)
        {
            StatusText = $"Install failed: {ex.Message}";
        }
    }

    public IReadOnlyList<string> CurrentSources()
        => _settingsStore.Current.SkillsSources ?? SkillsRepoCatalog.DefaultSources;

    public async Task UpdateSourcesAsync(IReadOnlyList<string> sources)
    {
        _settingsStore.Update(_settingsStore.Current with { SkillsSources = new List<string>(sources) });
        await LoadInternalAsync(forceRefresh: true);
    }

    private async Task LoadInternalAsync(bool forceRefresh)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        State = LoadState.Loading;
        ErrorText = string.Empty;

        try
        {
            if (forceRefresh)
                await _repoCatalog.RefreshAsync(cts.Token);

            var skills = await _repoCatalog.GetAllAsync(cts.Token);
            if (cts.IsCancellationRequested) return;

            Skills.Clear();
            _skillByDedupKey.Clear();

            var categorySet = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in skills)
            {
                _skillByDedupKey[s.DedupKey] = s;
                var category = DeriveCategory(s);
                categorySet.Add(category);
                Skills.Add(new SkillRow(
                    Slug: s.DedupKey,
                    Name: s.Name,
                    Source: s.Source,
                    SourceUrl: $"https://github.com/{s.Source}/tree/{s.Branch}/{s.FolderPath}",
                    Description: s.Description ?? string.Empty,
                    Category: category));
            }

            // Rebuild category list, preserving the user's pick if it still exists.
            var keep = SelectedCategory;
            Categories.Clear();
            Categories.Add(AllCategoriesLabel);
            foreach (var c in categorySet) Categories.Add(c);
            SelectedCategory = Categories.Contains(keep) ? keep : AllCategoriesLabel;

            // Surface per-source errors so a single broken source is visible.
            var snapshot = _repoCatalog.Snapshot();
            var errored = snapshot.Where(s => !string.IsNullOrEmpty(s.Error))
                                  .Select(s => $"{s.Source}: {s.Error}");
            ErrorText = string.Join(" | ", errored);

            State = Skills.Count == 0 ? LoadState.Empty : LoadState.Idle;
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            State = LoadState.Error;
        }
    }

    // Best-effort categorisation. Three sources of signal, in priority order:
    //   1. The skill's repo-relative folder path's first segment, if it has
    //      one (e.g. davila7's `agents/foo` → "agents").
    //   2. The owning repo's name ("awesome-claude-skills" → "Awesome").
    //   3. "General" as a last resort.
    private static string DeriveCategory(CommunitySkill s)
    {
        var folder = s.FolderPath ?? string.Empty;
        var firstSlash = folder.IndexOf('/');
        if (firstSlash > 0)
        {
            var head = folder[..firstSlash];
            // Skip noise prefixes that don't carry meaning.
            if (!IsNoiseSegment(head)) return Humanize(head);
        }

        // Fall back to the repo name (the part after the slash in `owner/repo`).
        var slash = s.Source?.IndexOf('/') ?? -1;
        if (slash > 0 && slash < s.Source!.Length - 1)
            return Humanize(s.Source[(slash + 1)..]);

        return "General";
    }

    private static bool IsNoiseSegment(string s)
        => s.Equals("plugins", StringComparison.OrdinalIgnoreCase)
        || s.Equals("src", StringComparison.OrdinalIgnoreCase)
        || s.Equals("skills", StringComparison.OrdinalIgnoreCase);

    private static string Humanize(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        var parts = raw.Split(new[] { '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
            if (parts[i].Length > 0) parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i][1..];
        return string.Join(' ', parts);
    }
}

public sealed record SkillRow(
    string Slug,
    string Name,
    string Source,
    string SourceUrl,
    string Description,
    string Category);
