using System.Windows.Controls;

namespace Drover.App.Views;

/// <summary>
/// Read-only launchpad for the plan → tasks → spec → loop pipeline. All
/// behaviour lives in <see cref="ViewModels.WorkflowPanelViewModel"/>.
/// </summary>
public partial class WorkflowPanel : UserControl
{
    public WorkflowPanel()
    {
        InitializeComponent();
    }
}
