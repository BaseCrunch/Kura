using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Kura.App.Services;
using Kura.Core.Net;

namespace Kura.App.Controls;

/// <summary>
/// Image that loads its source asynchronously through the shared <see cref="Kura.Media.ImageLoader"/>, decoded at
/// the cell's height times the monitor DPI. Recycled containers cancel stale loads when their URL changes.
/// </summary>
public sealed class ThumbnailImage : Image
{
    public static readonly DependencyProperty UrlProperty = DependencyProperty.Register(
        nameof(Url), typeof(string), typeof(ThumbnailImage), new PropertyMetadata(null, OnSourceInputChanged));

    public static readonly DependencyProperty RefererProperty = DependencyProperty.Register(
        nameof(Referer), typeof(string), typeof(ThumbnailImage), new PropertyMetadata(null));

    public static readonly DependencyProperty DecodeHeightProperty = DependencyProperty.Register(
        nameof(DecodeHeight), typeof(double), typeof(ThumbnailImage), new PropertyMetadata(200.0, OnSourceInputChanged));

    public static readonly DependencyProperty IsFailedProperty = DependencyProperty.Register(
        nameof(IsFailed), typeof(bool), typeof(ThumbnailImage), new PropertyMetadata(false));

    private CancellationTokenSource? _cts;
    private string? _loadedKey;

    public ThumbnailImage()
    {
        Stretch = Stretch.UniformToFill;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        Loaded += (_, _) => Reload();
        Unloaded += (_, _) => { _cts?.Cancel(); _loadedKey = null; };
    }

    public string? Url { get => (string?)GetValue(UrlProperty); set => SetValue(UrlProperty, value); }
    public string? Referer { get => (string?)GetValue(RefererProperty); set => SetValue(RefererProperty, value); }
    public double DecodeHeight { get => (double)GetValue(DecodeHeightProperty); set => SetValue(DecodeHeightProperty, value); }
    public bool IsFailed { get => (bool)GetValue(IsFailedProperty); set => SetValue(IsFailedProperty, value); }

    private static void OnSourceInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ThumbnailImage)d).Reload();

    private async void Reload()
    {
        if (!IsLoaded) return;
        var url = Url;
        var px = (int)Math.Ceiling(DecodeHeight * VisualTreeHelper.GetDpi(this).DpiScaleY);
        var key = url is null ? null : $"{Kura.Media.ImageLoader.Bucket(px)}|{url}";
        if (key == _loadedKey) return;
        _loadedKey = key;
        _cts?.Cancel();
        IsFailed = false;
        BeginAnimation(OpacityProperty, null);
        if (url is null) { Source = null; return; }

        var cts = _cts = new CancellationTokenSource();
        var task = AppServices.Current.Images.LoadThumbnailAsync(url, Referer, px, cts.Token);
        if (task.IsCompletedSuccessfully)
        {
            // RAM hit: show immediately, no fade.
            Source = task.Result;
            Opacity = 1;
            return;
        }
        Source = null;
        try
        {
            var bmp = await task;
            if (cts.IsCancellationRequested) return;
            Source = bmp;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new QuadraticEase() });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is BooruRequestException or IOException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            if (!cts.IsCancellationRequested) IsFailed = true;
        }
    }
}
