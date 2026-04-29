using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Drover.App.Models;
using Drover.App.ViewModels;

namespace Drover.App.Views;

public partial class FileExplorerPanel : UserControl
{
    public FileExplorerPanel()
    {
        InitializeComponent();
    }

    private ShellViewModel? Vm => DataContext as ShellViewModel;
    private FileNode? Selected => Tree.SelectedItem as FileNode;

    private void Tree_KeyDown(object sender, KeyEventArgs e)
    {
        var vm = Vm;
        if (vm is null) return;
        var node = Selected;
        switch (e.Key)
        {
            case Key.F5:
                vm.RefreshFileExplorerCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Enter when node is not null:
                if (node.IsDirectory) node.IsExpanded = !node.IsExpanded;
                else vm.OpenFileNode(node);
                e.Handled = true;
                break;
            case Key.C when Keyboard.Modifiers == ModifierKeys.Control && node is not null:
                CopyToClipboard(node.FullPath);
                e.Handled = true;
                break;
            case Key.C when Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && node is not null:
                CopyToClipboard(node.RelativePath);
                e.Handled = true;
                break;
        }
    }

    private void Tree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var node = Selected;
        if (node is null) return;
        // TreeView's default expand-on-double-click already toggles directories; we just
        // override for files so they open in the OS default handler.
        if (!node.IsDirectory)
        {
            Vm?.OpenFileNode(node);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Right-click should select the node under the cursor before the context menu opens —
    /// otherwise the menu acts on whatever was previously selected (or nothing).
    /// </summary>
    private void Tree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject src) return;
        var item = FindAncestor<TreeViewItem>(src);
        if (item is not null) item.IsSelected = true;
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T t) return t;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private void OpenInExplorer_Click(object sender, RoutedEventArgs e)
    {
        var vm = Vm;
        if (vm?.SelectedTab is not null) vm.OpenTabFolder(vm.SelectedTab);
    }

    private void InsertAtPath_Click(object sender, RoutedEventArgs e) => Vm?.InsertAtPathReference(Selected);
    private void OpenNode_Click(object sender, RoutedEventArgs e) => Vm?.OpenFileNode(Selected);
    private void RevealNode_Click(object sender, RoutedEventArgs e) => Vm?.RevealFileNode(Selected);

    private void CopyFullPath_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } n) CopyToClipboard(n.FullPath);
    }

    private void CopyRelPath_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } n) CopyToClipboard(n.RelativePath);
    }

    private void CopyToClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch { /* clipboard busy — ignore */ }
    }
}
