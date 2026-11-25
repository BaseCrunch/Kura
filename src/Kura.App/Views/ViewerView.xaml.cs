using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Kura.App.ViewModels;

namespace Kura.App.Views;

/// <summary>
/// Full keyboard viewer (SPEC 4.4 V3): ←/→ navigate, Home/End, Esc back, O original, Space play/pause,
/// M mute, Ctrl+0 fit, Ctrl+1 actual size, +/− zoom, mouse back/forward buttons navigate.
/// </summary>
public partial class ViewerView : UserControl
{
    private readonly DispatcherTimer _clock;
    private bool _seeking;

    public ViewerView()
    {
        InitializeComponent();
        _clock = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => UpdateClock(), Dispatcher);
        _clock.Stop();
        DependencyPropertyDescriptor.FromProperty(Controls.VideoHost.PositionProperty, typeof(Controls.VideoHost))
            .AddValueChanged(Video, (_, _) => UpdateClock());
    }

    private ViewerViewModel? Vm => DataContext as ViewerViewModel;

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            _clock.Start();
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Focus());
        }
        else _clock.Stop();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var vm = Vm;
        if (vm is null || !vm.IsOpen) { base.OnPreviewKeyDown(e); return; }
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var isVideo = vm.Content == ViewerContent.Video;
        switch (e.Key)
        {
            case Key.Right when isVideo && Keyboard.Modifiers == ModifierKeys.Shift: Video.SeekBy(5); break;
            case Key.Left when isVideo && Keyboard.Modifiers == ModifierKeys.Shift: Video.SeekBy(-5); break;
            case Key.Right or Key.PageDown: vm.NextCommand.Execute(null); break;
            case Key.Left or Key.PageUp: vm.PreviousCommand.Execute(null); break;
            case Key.Home: vm.FirstCommand.Execute(null); break;
            case Key.End: vm.LastCommand.Execute(null); break;
            case Key.Escape or Key.Back: vm.CloseCommand.Execute(null); break;
            case Key.O when !ctrl: vm.ToggleOriginalCommand.Execute(null); break;
            case Key.Space when isVideo: Video.TogglePlay(); break;
            case Key.M when isVideo: vm.IsMuted = !vm.IsMuted; break;
            case Key.D0 or Key.NumPad0 when ctrl: Zoom.FitToView(); break;
            case Key.D1 or Key.NumPad1 when ctrl: Zoom.ActualSize(); break;
            case Key.OemPlus or Key.Add: Zoom.ZoomBy(1.25); break;
            case Key.OemMinus or Key.Subtract: Zoom.ZoomBy(0.8); break;
            case Key.F: vm.ShowHint("Favorites arrive in Phase 2"); break;
            case Key.D when !ctrl: vm.ShowHint("Downloads arrive in Phase 2"); break;
            case Key.N: vm.ShowHint("Notes overlay arrives in Phase 3"); break;
            default: base.OnPreviewKeyDown(e); return;
        }
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (Vm is { } vm)
        {
            if (e.ChangedButton == MouseButton.XButton1) { vm.PreviousCommand.Execute(null); e.Handled = true; }
            else if (e.ChangedButton == MouseButton.XButton2) { vm.NextCommand.Execute(null); e.Handled = true; }
        }
        Focus();
        base.OnMouseUp(e);
    }

    private void OnFit(object sender, RoutedEventArgs e) => Zoom.FitToView();
    private void OnActual(object sender, RoutedEventArgs e) => Zoom.ActualSize();
    private void OnZoomIn(object sender, RoutedEventArgs e) => Zoom.ZoomBy(1.25);
    private void OnZoomOut(object sender, RoutedEventArgs e) => Zoom.ZoomBy(0.8);
    private void OnTogglePlay(object sender, RoutedEventArgs e) { Video.TogglePlay(); Focus(); }

    private void OnToggleMute(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.IsMuted = !vm.IsMuted;
        Focus();
    }

    private void OnSeekDragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e) => _seeking = true;

    private void OnSeekDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _seeking = false;
        Video.SeekTo(Seek.Value);
        Focus();
    }

    private void OnSeekClicked(object sender, MouseButtonEventArgs e)
    {
        if (_seeking) return;
        Video.SeekTo(Seek.Value);
        Focus();
    }

    private void UpdateClock()
    {
        if (!_seeking) Seek.Value = Video.Position;
        PosText.Text = Format(Video.Position);
        DurText.Text = Format(Video.Duration);
    }

    private static string Format(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}
