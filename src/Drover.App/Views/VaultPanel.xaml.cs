using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Drover.App.ViewModels;
using Drover.Vault.Core.Models;

namespace Drover.App.Views;

// Right-pane vault panel. Hosts the same UI as the old VaultWindow but
// docked into the shell — DataContext is the singleton VaultViewModel
// exposed on ShellViewModel.Vault. Tab/file switching with a dirty
// editor still prompts (save/discard/cancel); the prompt's owner is the
// nearest Window so it parents correctly off the shell.
public partial class VaultPanel : UserControl
{
    private bool _suppressTabChange;
    private bool _scopeSyncSubscribed;

    public VaultPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += (_, _) => SyncScopeTab();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_scopeSyncSubscribed && DataContext is VaultViewModel vm)
        {
            vm.PropertyChanged += OnVmPropertyChanged;
            _scopeSyncSubscribed = true;
        }
        SyncScopeTab();
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VaultViewModel.Scope) || e.PropertyName == nameof(VaultViewModel.ProjectScopeAvailable))
        {
            // Marshal to UI thread — VaultStore.Changed fires on background.
            Dispatcher.BeginInvoke(new Action(SyncScopeTab));
        }
    }

    private void SyncScopeTab()
    {
        if (DataContext is not VaultViewModel vm) return;
        ProjectTab.IsEnabled = vm.ProjectScopeAvailable;
        _suppressTabChange = true;
        try
        {
            ScopeTabs.SelectedItem = vm.Scope == VaultScope.Project && vm.ProjectScopeAvailable
                ? ProjectTab
                : GlobalTab;
        }
        finally { _suppressTabChange = false; }
    }

    private void ScopeTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != ScopeTabs) return;
        if (_suppressTabChange) return;
        if (DataContext is not VaultViewModel vm) return;
        if (ScopeTabs.SelectedItem is not TabItem item) return;

        var nextScope = ReferenceEquals(item, ProjectTab) ? VaultScope.Project : VaultScope.Global;
        if (nextScope == vm.Scope) return;

        if (vm.IsDirty && vm.SelectedEntry is { IsFolder: false } sel)
        {
            var owner = Window.GetWindow(this);
            var result = MessageBox.Show(owner,
                $"{sel.RelativePath} has unsaved changes. Save before switching scope?",
                "Vault — unsaved changes",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Cancel)
            {
                RevertTab(e, vm);
                return;
            }
            if (result == MessageBoxResult.Yes)
            {
                if (vm.SaveCommand.CanExecute(null))
                    vm.SaveCommand.Execute(null);
                if (vm.IsDirty)
                {
                    RevertTab(e, vm);
                    return;
                }
            }
        }

        vm.Scope = nextScope;
    }

    private void RevertTab(SelectionChangedEventArgs e, VaultViewModel vm)
    {
        _suppressTabChange = true;
        try
        {
            ScopeTabs.SelectedItem = vm.Scope == VaultScope.Project ? ProjectTab : GlobalTab;
        }
        finally { _suppressTabChange = false; }
        e.Handled = true;
    }

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (sender is not TreeView tree) return;
        if (DataContext is not VaultViewModel vm) return;
        if (tree.SelectedItem is VaultTreeNode node)
        {
            if (vm.IsDirty
                && vm.SelectedEntry is { IsFolder: false } prev
                && !ReferenceEquals(prev, node))
            {
                var owner = Window.GetWindow(this);
                var result = MessageBox.Show(owner,
                    $"{prev.RelativePath} has unsaved changes. Save before switching files?",
                    "Vault — unsaved changes",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Cancel)
                {
                    node.IsSelected = false;
                    prev.IsSelected = true;
                    return;
                }
                if (result == MessageBoxResult.Yes && vm.SaveCommand.CanExecute(null))
                    vm.SaveCommand.Execute(null);
            }
            vm.SelectedEntry = node;
        }
        else
        {
            vm.SelectedEntry = null;
        }
    }

    private void NewNote_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not VaultViewModel vm) return;
        var owner = Window.GetWindow(this);
        var dialog = new NewVaultNoteDialog(VaultViewModel.DefaultNewNoteSuggestion(), TryCreate);
        if (owner is not null) dialog.Owner = owner;
        dialog.ShowDialog();

        bool TryCreate(string path, out string error)
            => vm.TryCreateNote(path, out error);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (DataContext is VaultViewModel vm
            && e.Key == Key.S
            && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (vm.SaveCommand.CanExecute(null))
            {
                vm.SaveCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }
        base.OnPreviewKeyDown(e);
    }
}
