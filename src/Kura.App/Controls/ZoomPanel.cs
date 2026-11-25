using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Kura.App.Controls;

/// <summary>
/// Hosts one child at its natural size and shows it through a scale+translate transform: fit-to-view by default,
/// mouse-wheel zoom around the cursor, middle- or left-drag panning, double-click to toggle fit and 1:1
/// (SPEC 4.4 V3). "1:1" means one image pixel per physical screen pixel.
/// </summary>
public sealed class ZoomPanel : Panel
{
    public static readonly DependencyProperty ResetTokenProperty = DependencyProperty.Register(
        nameof(ResetToken), typeof(int), typeof(ZoomPanel), new PropertyMetadata(0, (d, _) => ((ZoomPanel)d).FitToView()));

    private const double MinScale = 0.02;
    private const double MaxScale = 32;
    private readonly MatrixTransform _transform = new();
    private double _scale = 1;
    private Vector _offset;
    private bool _isFit = true;
    private Point? _panStart;
    private Vector _panOrigin;

    public ZoomPanel()
    {
        ClipToBounds = true;
        Background = Brushes.Transparent; // hit-testable everywhere for wheel/drag
        Focusable = false;
    }

    public int ResetToken { get => (int)GetValue(ResetTokenProperty); set => SetValue(ResetTokenProperty, value); }

    public bool IsFit => _isFit;

    private UIElement? Child => InternalChildren.Count > 0 ? InternalChildren[0] : null;

    protected override Size MeasureOverride(Size availableSize)
    {
        Child?.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return new Size(
            double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is { } c)
        {
            c.Arrange(new Rect(new Point(0, 0), c.DesiredSize));
            c.RenderTransform = _transform;
        }
        if (_isFit) FitToView();
        else Clamp();
        return finalSize;
    }

    private Size ContentSize => Child?.DesiredSize ?? default;

    private double FitScale
    {
        get
        {
            var c = ContentSize;
            if (c.Width <= 0 || c.Height <= 0 || ActualWidth <= 0 || ActualHeight <= 0) return 1;
            return Math.Min(ActualWidth / c.Width, ActualHeight / c.Height);
        }
    }

    private double ActualSizeScale => 1 / VisualTreeHelper.GetDpi(this).DpiScaleX;

    public void FitToView()
    {
        _isFit = true;
        _scale = FitScale;
        Center();
        Apply();
    }

    public void ActualSize(Point? around = null) => ZoomTo(ActualSizeScale, around);

    public void ZoomBy(double factor, Point? around = null) => ZoomTo(_scale * factor, around);

    public void ZoomTo(double scale, Point? around = null)
    {
        scale = Math.Clamp(scale, Math.Min(MinScale, FitScale), MaxScale);
        var p = around ?? new Point(ActualWidth / 2, ActualHeight / 2);
        // Keep the content point under the cursor fixed while scaling.
        var contentPoint = (p - (Point)_offset) / _scale;
        _scale = scale;
        _offset = (Vector)p - contentPoint * _scale;
        _isFit = Math.Abs(_scale - FitScale) < 0.0001;
        Clamp();
        Apply();
    }

    private void Center()
    {
        var c = ContentSize;
        _offset = new Vector((ActualWidth - c.Width * _scale) / 2, (ActualHeight - c.Height * _scale) / 2);
    }

    /// <summary>Centers along an axis where the content is smaller than the view; otherwise keeps edges covered.</summary>
    private void Clamp()
    {
        var c = ContentSize;
        var w = c.Width * _scale;
        var h = c.Height * _scale;
        _offset.X = w <= ActualWidth ? (ActualWidth - w) / 2 : Math.Clamp(_offset.X, ActualWidth - w, 0);
        _offset.Y = h <= ActualHeight ? (ActualHeight - h) / 2 : Math.Clamp(_offset.Y, ActualHeight - h, 0);
    }

    private void Apply() => _transform.Matrix = new Matrix(_scale, 0, 0, _scale, _offset.X, _offset.Y);

    private bool CanPan
    {
        get
        {
            var c = ContentSize;
            return c.Width * _scale > ActualWidth + 0.5 || c.Height * _scale > ActualHeight + 0.5;
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        ZoomBy(Math.Pow(1.18, e.Delta / 120.0), e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
        {
            if (_isFit || Math.Abs(_scale - FitScale) < 0.01) ActualSize(e.GetPosition(this));
            else FitToView();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && CanPan))
        {
            _panStart = e.GetPosition(this);
            _panOrigin = _offset;
            CaptureMouse();
            Cursor = Cursors.SizeAll;
            e.Handled = true;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_panStart is { } start)
        {
            _offset = _panOrigin + (e.GetPosition(this) - start);
            _isFit = false;
            Clamp();
            Apply();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_panStart is not null)
        {
            _panStart = null;
            ReleaseMouseCapture();
            Cursor = null;
            e.Handled = true;
        }
        base.OnMouseUp(e);
    }
}
