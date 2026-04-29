using System;
using System.Collections;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Drover.App.Views;

/// <summary>
/// TabControl that keeps every item's content realized and alive, toggling only
/// visibility on selection change. Default WPF TabControl reuses a single
/// ContentPresenter and swaps DataContext — which unloads hosted controls like
/// DroverTerminal on every tab switch, tearing down their PTYs.
/// Also supports drag-reorder on the tab headers by calling Move(int,int) on the
/// underlying ObservableCollection via reflection.
/// </summary>
public class TabControlEx : TabControl
{
    private const double DragThreshold = 6.0;

    private Panel? _itemsHolder;
    private Point _dragStart;
    private TabItem? _draggingTab;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _itemsHolder = GetTemplateChild("PART_ItemsHolder") as Panel;
        RefreshContent();
    }

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        if (element is TabItem ti)
        {
            ti.PreviewMouseLeftButtonDown -= OnTabMouseDown;
            ti.PreviewMouseMove -= OnTabMouseMove;
            ti.PreviewMouseLeftButtonUp -= OnTabMouseUp;
            ti.PreviewMouseLeftButtonDown += OnTabMouseDown;
            ti.PreviewMouseMove += OnTabMouseMove;
            ti.PreviewMouseLeftButtonUp += OnTabMouseUp;
        }
    }

    private void OnTabMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TabItem ti)
        {
            _draggingTab = ti;
            _dragStart = e.GetPosition(this);
        }
    }

    private void OnTabMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingTab is null || e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _dragStart.X) < DragThreshold && Math.Abs(pos.Y - _dragStart.Y) < DragThreshold)
            return;

        var over = HitTestTabItem(pos);
        if (over is null || ReferenceEquals(over, _draggingTab)) return;

        MoveItem(_draggingTab.DataContext, over.DataContext);
    }

    private void OnTabMouseUp(object sender, MouseButtonEventArgs e)
    {
        _draggingTab = null;
    }

    private TabItem? HitTestTabItem(Point pos)
    {
        var hit = VisualTreeHelper.HitTest(this, pos);
        var visual = hit?.VisualHit;
        while (visual is not null)
        {
            if (visual is TabItem ti) return ti;
            visual = VisualTreeHelper.GetParent(visual);
        }
        return null;
    }

    private void MoveItem(object? fromItem, object? toItem)
    {
        if (fromItem is null || toItem is null) return;
        if (ItemsSource is not IList list) return;

        int from = list.IndexOf(fromItem);
        int to = list.IndexOf(toItem);
        if (from < 0 || to < 0 || from == to) return;

        var moveMethod = list.GetType().GetMethod("Move", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int), typeof(int) }, null);
        if (moveMethod is not null)
        {
            moveMethod.Invoke(list, new object[] { from, to });
        }
    }

    protected override void OnItemsChanged(NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(e);

        if (_itemsHolder is null) return;

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _itemsHolder.Children.Clear();
        }
        else if (e.OldItems is not null)
        {
            foreach (var old in e.OldItems)
            {
                var cp = FindPresenter(old);
                if (cp is not null) _itemsHolder.Children.Remove(cp);
            }
        }

        RefreshContent();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Default TabControl swallows Home/End/Left/Right/Up/Down/PageUp/PageDown
        // for tab navigation when a TabItem has focus. We don't want that — Home
        // should reach the terminal so it jumps to the start of the line. Tab
        // cycling is handled via Ctrl+Tab / Ctrl+1..9 elsewhere.
        switch (e.Key)
        {
            case Key.Home:
            case Key.End:
            case Key.Left:
            case Key.Right:
            case Key.Up:
            case Key.Down:
            case Key.PageUp:
            case Key.PageDown:
                return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        UpdateVisibility();
        FocusSelectedContent();
    }

    private void FocusSelectedContent()
    {
        if (_itemsHolder is null) return;
        var visible = _itemsHolder.Children.OfType<ContentPresenter>()
            .FirstOrDefault(cp => ReferenceEquals(cp.Tag, SelectedItem));
        if (visible is null) return;

        // The terminal hwnd may not be live yet on first switch; defer until layout.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var target = FindFocusTarget(visible);
            target?.Focus();
        }), DispatcherPriority.Input);
    }

    private static IInputElement? FindFocusTarget(DependencyObject root)
    {
        // Prefer DroverTerminal if present (matches the GotFocus → Terminal.Focus()
        // path it sets up internally). Fall back to any focusable descendant.
        var queue = new System.Collections.Generic.Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node is Drover.App.Terminal.DroverTerminal dt) return dt;
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
                queue.Enqueue(VisualTreeHelper.GetChild(node, i));
        }
        return root as IInputElement;
    }

    private void RefreshContent()
    {
        if (_itemsHolder is null) return;

        foreach (var item in Items)
        {
            if (FindPresenter(item) is not null) continue;

            var cp = new ContentPresenter
            {
                Content = item,
                ContentTemplate = ContentTemplate,
                ContentTemplateSelector = ContentTemplateSelector,
                Tag = item
            };
            _itemsHolder.Children.Add(cp);
        }

        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        if (_itemsHolder is null) return;
        foreach (ContentPresenter cp in _itemsHolder.Children)
        {
            cp.Visibility = ReferenceEquals(cp.Tag, SelectedItem) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private ContentPresenter? FindPresenter(object? item)
    {
        if (_itemsHolder is null) return null;
        return _itemsHolder.Children.OfType<ContentPresenter>().FirstOrDefault(x => ReferenceEquals(x.Tag, item));
    }
}
