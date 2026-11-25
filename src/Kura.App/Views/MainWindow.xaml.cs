using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kura.App.ViewModels;

namespace Kura.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        StateChanged += (_, _) => ApplyWindowState();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => ApplyPanelWidth();
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel old)
        {
            old.ServerEditorRequested -= OpenServerEditor;
            old.PropertyChanged -= OnVmPropertyChanged;
        }
        if (e.NewValue is MainViewModel vm)
        {
            vm.ServerEditorRequested += OpenServerEditor;
            vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.InfoPanelVisible)) ApplyPanelWidth();
    }

    private void ApplyPanelWidth()
    {
        PanelCol.Width = Vm.InfoPanelVisible ? new GridLength(Math.Clamp(Vm.InfoPanelWidth, 240, 600)) : new GridLength(0);
        PanelCol.MinWidth = Vm.InfoPanelVisible ? 240 : 0;
    }

    private void OnSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) =>
        Vm.InfoPanelWidth = PanelCol.ActualWidth;

    private void ApplyWindowState()
    {
        // A maximised WindowChrome window overhangs the screen by the resize border; pad it back in.
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        Root.BorderThickness = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(1);
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnTabMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SearchTabViewModel tab }) Vm.SelectedTab = tab;
    }

    private void OnTabMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && sender is FrameworkElement { DataContext: SearchTabViewModel tab })
        {
            Vm.CloseTab(tab);
            e.Handled = true;
        }
    }

    private void OnTabStripWheel(object sender, MouseWheelEventArgs e)
    {
        TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void OpenServerEditor()
    {
        var vm = new ServerEditorViewModel(Vm.Services);
        var dlg = new ServerEditorWindow { DataContext = vm, Owner = this };
        using (Vm.Services.Overlay.Enter()) dlg.ShowDialog();
        if (vm.Changed) Vm.ReloadServers();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Ctrl+L / F6 focuses the search box of the active tab.
        if ((e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control) || e.Key == Key.F6)
        {
            SearchTabView.FocusSearchOf(Vm.SelectedTab);
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }
}
