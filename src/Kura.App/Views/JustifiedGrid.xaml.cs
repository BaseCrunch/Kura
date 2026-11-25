using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Kura.App.Services;
using Kura.App.ViewModels;
using Kura.Core.Layout;

namespace Kura.App.Views;

public sealed class GridCell(PostItemViewModel item, double width, double height, Thickness margin)
{
    public PostItemViewModel Item { get; } = item;
    public double Width { get; } = width;
    public double Height { get; } = height;
    public Thickness Margin { get; } = margin;
}

public sealed class GridRow(int start, double height, IReadOnlyList<GridCell> cells, bool isComplete)
{
    public int Start { get; } = start;
    public double Height { get; } = height;
    public IReadOnlyList<GridCell> Cells { get; } = cells;
    public bool IsComplete { get; } = isComplete;
}

/// <summary>
/// Virtualized justified grid (SPEC 4.3 G2). Rows are packed from aspect ratios and hosted in a pixel-scrolling
/// <see cref="VirtualizingStackPanel"/>, which keeps 2 pages realised above and below the viewport so thumbnails
/// prefetch ahead of the scroll position (G4).
/// </summary>
public partial class JustifiedGrid : UserControl
{
    private const double Spacing = 6;
    private const double RowGap = 6;
    private const double PanelPadding = 16 + 6; // VirtualizingStackPanel left + right margin

    private readonly ObservableCollection<GridRow> _rows = [];
    private readonly List<double> _rowTops = [];
    private readonly DispatcherTimer _relayoutTimer;
    private SearchTabViewModel? _tab;
    private ScrollViewer? _scroller;
    private double _layoutWidth;
    private int _anchorIndex = -1;

    public JustifiedGrid()
    {
        InitializeComponent();
        Rows.ItemsSource = _rows;
        _relayoutTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Background, (_, _) => RelayoutNow(), Dispatcher);
        _relayoutTimer.Stop();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        PreviewMouseWheel += OnPreviewMouseWheel;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_scroller is not null) return;
        Rows.ApplyTemplate();
        _scroller = (ScrollViewer)Rows.Template.FindName("PART_Scroller", Rows);
        _scroller.ScrollChanged += OnScrollChanged;
        _scroller.SizeChanged += (_, _) => ScheduleRelayout();
        RelayoutNow();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_tab is not null)
        {
            _tab.Posts.CollectionChanged -= OnPostsChanged;
            _tab.Main.PropertyChanged -= OnMainChanged;
            _tab.Viewer.ClosedAt -= ScrollIntoView;
        }
        _tab = e.NewValue as SearchTabViewModel;
        if (_tab is not null)
        {
            _tab.Posts.CollectionChanged += OnPostsChanged;
            _tab.Main.PropertyChanged += OnMainChanged;
            _tab.Viewer.ClosedAt += ScrollIntoView;
        }
        RelayoutNow();
    }

    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ThumbnailHeight)) ScheduleRelayout(keepAnchor: true);
    }

    private double TargetHeight => _tab?.Main.ThumbnailHeight ?? 220;

    private double AvailableWidth()
    {
        if (_scroller is null) return 0;
        var w = _scroller.ViewportWidth;
        if (w <= 0 || double.IsNaN(w)) w = _scroller.ActualWidth - 10;
        return Math.Floor(w - PanelPadding);
    }

    private void OnPostsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewStartingIndex == (_tab!.Posts.Count - e.NewItems!.Count):
                Append();
                break;
            case NotifyCollectionChangedAction.Reset:
                _rows.Clear();
                _rowTops.Clear();
                _scroller?.ScrollToTop();
                break;
            default:
                RelayoutNow();
                break;
        }
    }

    private void ScheduleRelayout(bool keepAnchor = true)
    {
        if (keepAnchor && _anchorIndex < 0) _anchorIndex = FirstVisibleIndex();
        _relayoutTimer.Stop();
        _relayoutTimer.Start();
    }

    /// <summary>Re-packs only from the last incomplete row (a new page usually just extends the bottom).</summary>
    private void Append()
    {
        var width = AvailableWidth();
        if (width <= 0 || Math.Abs(width - _layoutWidth) > 0.5) { RelayoutNow(); return; }
        var from = 0;
        if (_rows.Count > 0)
        {
            var last = _rows[^1];
            if (!last.IsComplete)
            {
                from = last.Start;
                _rows.RemoveAt(_rows.Count - 1);
                _rowTops.RemoveAt(_rowTops.Count - 1);
            }
            else from = last.Start + last.Cells.Count;
        }
        AddRows(from, width);
    }

    private void RelayoutNow()
    {
        _relayoutTimer.Stop();
        var anchor = _anchorIndex >= 0 ? _anchorIndex : FirstVisibleIndex();
        _anchorIndex = -1;
        _rows.Clear();
        _rowTops.Clear();
        var width = AvailableWidth();
        _layoutWidth = width;
        if (_tab is null || width <= 0 || _tab.Posts.Count == 0) return;
        AddRows(0, width);
        if (anchor > 0) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ScrollToIndex(anchor, topAligned: true));
    }

    private void AddRows(int from, double width)
    {
        var posts = _tab!.Posts;
        var aspects = new double[posts.Count];
        for (var i = 0; i < posts.Count; i++) aspects[i] = posts[i].Aspect;
        var packed = JustifiedLayout.Pack(aspects, width, TargetHeight, Spacing, from);
        var top = _rowTops.Count > 0 ? _rowTops[^1] + _rows[^1].Height + RowGap : 0;
        foreach (var r in packed)
        {
            var cells = new GridCell[r.Count];
            for (var k = 0; k < r.Count; k++)
            {
                var margin = new Thickness(0, 0, k == r.Count - 1 ? 0 : Spacing, 0);
                cells[k] = new GridCell(posts[r.Start + k], r.Widths[k], r.Height, margin);
            }
            _rows.Add(new GridRow(r.Start, r.Height, cells, r.IsComplete));
            _rowTops.Add(top);
            top += r.Height + RowGap;
        }
    }

    // ----- Scrolling -----

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_tab is null || _scroller is null) return;
        if (e.ViewportWidthChange != 0) ScheduleRelayout();
        var remaining = _scroller.ExtentHeight - _scroller.VerticalOffset - _scroller.ViewportHeight;
        _tab.OnScrolled(remaining, Math.Max(1, _scroller.ViewportHeight), FirstVisibleIndex());
    }

    private int RowAtOffset(double offset)
    {
        if (_rowTops.Count == 0) return -1;
        var lo = 0;
        var hi = _rowTops.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (_rowTops[mid] <= offset) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    private int FirstVisibleIndex()
    {
        if (_scroller is null) return -1;
        var row = RowAtOffset(_scroller.VerticalOffset);
        return row < 0 ? -1 : _rows[row].Start;
    }

    public void ScrollIntoView(int index) => ScrollToIndex(index, topAligned: false);

    private void ScrollToIndex(int index, bool topAligned)
    {
        if (_scroller is null || index < 0) return;
        for (var r = 0; r < _rows.Count; r++)
        {
            var row = _rows[r];
            if (index < row.Start || index >= row.Start + row.Cells.Count) continue;
            var top = _rowTops[r];
            var bottom = top + row.Height;
            if (topAligned) _scroller.ScrollToVerticalOffset(top);
            else if (top < _scroller.VerticalOffset || bottom > _scroller.VerticalOffset + _scroller.ViewportHeight)
                _scroller.ScrollToVerticalOffset(Math.Max(0, top - _scroller.ViewportHeight / 3));
            return;
        }
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_tab is null || _scroller is null) return;
        if (Keyboard.Modifiers != ModifierKeys.Control)
        {
            // VirtualizingStackPanel's own pixel-mode wheel step is tiny; scroll proportionally to the wheel delta
            // (120 per notch, smaller for precision touchpads/high-resolution wheels).
            _scroller.ScrollToVerticalOffset(_scroller.VerticalOffset - e.Delta * 1.25);
            e.Handled = true;
            return;
        }
        var main = _tab.Main;
        main.ThumbnailHeight = Math.Clamp(main.ThumbnailHeight + (e.Delta > 0 ? 20 : -20), 100, 480);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_scroller is null) { base.OnKeyDown(e); return; }
        switch (e.Key)
        {
            case Key.PageDown: _scroller.PageDown(); break;
            case Key.PageUp: _scroller.PageUp(); break;
            case Key.Home: _scroller.ScrollToTop(); break;
            case Key.End: _scroller.ScrollToBottom(); break;
            case Key.Down: _scroller.ScrollToVerticalOffset(_scroller.VerticalOffset + 120); break;
            case Key.Up: _scroller.ScrollToVerticalOffset(_scroller.VerticalOffset - 120); break;
            case Key.Enter when _tab?.FocusedPost is { } p: _tab.OpenViewer(p.Index); break;
            default: base.OnKeyDown(e); return;
        }
        e.Handled = true;
    }

    // ----- Cell interactions -----

    private static PostItemViewModel? ItemOf(object sender) =>
        sender is FrameworkElement { DataContext: GridCell c } ? c.Item
        : sender is MenuItem { DataContext: GridCell mc } ? mc.Item
        : null;

    private void OnCellEnter(object sender, MouseEventArgs e)
    {
        if (_tab is not null && ItemOf(sender) is { } item) _tab.FocusedPost = item;
    }

    private void OnCellClick(object sender, MouseButtonEventArgs e)
    {
        if (_tab is null || ItemOf(sender) is not { } item) return;
        Focus();
        _tab.OpenViewer(item.Index);
        e.Handled = true;
    }

    private void OnMenuOpenViewer(object sender, RoutedEventArgs e)
    {
        if (_tab is not null && ItemOf(sender) is { } item) _tab.OpenViewer(item.Index);
    }

    private void OnMenuOpenPage(object sender, RoutedEventArgs e) => Shell.OpenUrl(ItemOf(sender)?.Post.PostUrl);
    private void OnMenuCopyPostUrl(object sender, RoutedEventArgs e) => Shell.Copy(ItemOf(sender)?.Post.PostUrl);
    private void OnMenuCopyFileUrl(object sender, RoutedEventArgs e) => Shell.Copy(ItemOf(sender)?.Post.File.Url);

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        base.OnMouseDown(e);
    }

    internal static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null and not T) d = VisualTreeHelper.GetParent(d);
        return d as T;
    }
}
