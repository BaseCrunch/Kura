using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Kura.App.ViewModels;

namespace Kura.App.Views;

/// <summary>Search history flyout content.</summary>
public partial class HistoryPanel : UserControl
{
    public HistoryPanel()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Dispatcher.BeginInvoke(() => FilterBox.Focus());
        };
    }

    private HistoryViewModel? Vm => DataContext as HistoryViewModel;

    private static bool FromButton(object source) => source is DependencyObject d && JustifiedGrid.FindAncestor<ButtonBase>(d) is not null;

    private void OnItemClick(object sender, MouseButtonEventArgs e)
    {
        if (FromButton(e.OriginalSource) || (sender as FrameworkElement)?.DataContext is not HistoryItemViewModel item) return;
        Vm?.Run(item, newTab: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        e.Handled = true;
    }

    private void OnItemMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || (sender as FrameworkElement)?.DataContext is not HistoryItemViewModel item) return;
        Vm?.Run(item, newTab: true);
        e.Handled = true;
    }
}
