using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kura.App.ViewModels;

namespace Kura.App.Views;

/// <summary>Home page of pinned searches.</summary>
public partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();

    private HomeViewModel? Vm => DataContext as HomeViewModel;

    private void OnPinTitleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PinViewModel pin) pin.OpenCommand.Execute(null);
    }

    private void OnThumbClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PinThumb thumb) return;
        var pin = Vm?.Pins.FirstOrDefault(p => p.Thumbs.Contains(thumb));
        if (pin is not null) _ = pin.OpenAtAsync(thumb);
        e.Handled = true;
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    /// <summary>The wheel scrolls the page; Shift+wheel scrolls the strip sideways.</summary>
    private void OnStripWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Shift && sender is ScrollViewer strip)
            strip.ScrollToHorizontalOffset(strip.HorizontalOffset - e.Delta);
        else
            PageScroller.ScrollToVerticalOffset(PageScroller.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private void OnNewQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm is { } vm)
        {
            vm.AddPinCommand.Execute(null);
            e.Handled = true;
        }
    }
}
