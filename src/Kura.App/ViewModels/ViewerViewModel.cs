using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kura.App.Services;
using Kura.Core.Models;
using Kura.Core.Net;
using Kura.Media;

namespace Kura.App.ViewModels;

public enum ViewerContent { None, Image, Animation, Video }

/// <summary>
/// Full-tab viewer over the owning tab's results: progressive image loading (thumbnail, then full),
/// frame-by-frame GIF playback, video state for the LibVLC host, and preloading (SPEC 3.4, 4.4).
/// </summary>
public sealed partial class ViewerViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly SearchTabViewModel _tab;
    private readonly DispatcherTimer _frameTimer;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _preloadCts;
    private AnimatedImage? _animation;
    private int _frame;
    private bool _tabActive;
    private BitmapSource? _nextFrame;
    private Task? _frameDecode;

    public ViewerViewModel(AppServices services, SearchTabViewModel tab)
    {
        _services = services;
        _tab = tab;
        _frameTimer = new DispatcherTimer(DispatcherPriority.Render);
        _frameTimer.Tick += OnFrameTick;
        _services.Overlay.Changed += _ => UpdateVideoAttachment();
        _isMuted = services.Settings.Get(SettingKeys.VideoMuted, false);
        _volume = services.Settings.Get(SettingKeys.VideoVolume, 80);
    }

    public SearchTabViewModel Tab => _tab;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private int _index = -1;
    [ObservableProperty] private PostItemViewModel? _current;
    [ObservableProperty] private ViewerContent _content;
    [ObservableProperty] private BitmapSource? _image;
    [ObservableProperty] private bool _isLoadingFull;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private bool _forceOriginal;
    [ObservableProperty] private string _positionText = "";
    [ObservableProperty] private string? _videoUrl;
    [ObservableProperty] private bool _videoShouldBeAttached;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private int _volume;
    [ObservableProperty] private string _qualityText = "";

    /// <summary>Layout size of the image in pixels; known before decoding so the thumbnail placeholder doesn't jump.</summary>
    [ObservableProperty] private double _displayWidth = 1;
    [ObservableProperty] private double _displayHeight = 1;

    /// <summary>Short transient hint shown in the top bar (e.g. for keys reserved for later phases).</summary>
    [ObservableProperty] private string? _hint;

    /// <summary>Incremented whenever the view should reset zoom (new post or new image source).</summary>
    [ObservableProperty] private int _resetZoomToken;

    partial void OnIsMutedChanged(bool value) => _services.Settings.Set(SettingKeys.VideoMuted, value);
    partial void OnVolumeChanged(int value) => _services.Settings.Set(SettingKeys.VideoVolume, value);

    public void Open(int index)
    {
        if (index < 0 || index >= _tab.Posts.Count) return;
        IsOpen = true;
        Show(index);
    }

    [RelayCommand]
    public void Close()
    {
        if (!IsOpen) return;
        if (Index >= 0 && Index < _tab.Posts.Count) _tab.FocusedPost = _tab.Posts[Index];
        StopAll();
        IsOpen = false;
        Content = ViewerContent.None;
        Image = null;
        VideoUrl = null;
        Current = null;
        UpdateVideoAttachment();
        ClosedAt?.Invoke(Index);
    }

    /// <summary>Raised on close with the last index, so the grid can scroll it into view.</summary>
    public event Action<int>? ClosedAt;

    [RelayCommand] private void Next() => Step(+1);
    [RelayCommand] private void Previous() => Step(-1);
    [RelayCommand] private void First() => Show(0);
    [RelayCommand] private void Last() => Show(_tab.Posts.Count - 1);

    private void Step(int delta)
    {
        var target = Index + delta;
        if (target >= _tab.Posts.Count)
        {
            _ = _tab.LoadMoreAsync(); // at the end of loaded results: fetch more, stay put
            return;
        }
        if (target >= 0) Show(target);
    }

    [RelayCommand]
    private void ToggleOriginal()
    {
        ForceOriginal = !ForceOriginal;
        if (Index >= 0) Show(Index);
    }

    [RelayCommand] private void OpenPostPage() => Shell.OpenUrl(Current?.Post.PostUrl);
    [RelayCommand] private void CopyFileUrl() => Shell.Copy(Current?.Post.File.Url);

    [RelayCommand]
    private void Retry()
    {
        if (Index >= 0) Show(Index);
    }

    private void Show(int index)
    {
        if (index < 0 || index >= _tab.Posts.Count) return;
        StopAll();
        var cts = _loadCts = new CancellationTokenSource();

        Index = index;
        var item = _tab.Posts[index];
        Current = item;
        _tab.FocusedPost = item;
        PositionText = $"{index + 1:N0} / {_tab.Posts.Count:N0}{(_tab.IsEndReached ? "" : "+")}";
        ErrorText = null;
        Progress = 0;
        ResetZoomToken++;

        var post = item.Post;
        var visible = post.VisibleImage(ForceOriginal || _tab.QueryForcesOriginal);
        QualityText = visible == post.File ? "Original" : "Sample";
        SetDisplaySize(visible, post);

        if (visible.Kind == MediaKind.Video)
        {
            Content = ViewerContent.Video;
            Image = item.ThumbUrl is { } tu ? _services.Images.PeekThumbnail(tu) : null;
            VideoUrl = visible.Url;
            IsLoadingFull = false;
            UpdateVideoAttachment();
        }
        else
        {
            VideoUrl = null;
            UpdateVideoAttachment();
            Content = ViewerContent.Image;
            Image = item.ThumbUrl is { } tu ? _services.Images.PeekThumbnail(tu) : null;
            _ = LoadImageAsync(visible, post.PostPageReferer, cts.Token);
        }

        if (index >= _tab.Posts.Count - 3) _ = _tab.LoadMoreAsync();
        Preload(index);
    }

    private void SetDisplaySize(PostImage visible, Post post)
    {
        if (visible.Width > 0 && visible.Height > 0)
        {
            DisplayWidth = visible.Width;
            DisplayHeight = visible.Height;
            return;
        }
        // Size unknown (e.g. Danbooru's large_file_url): scale the file's dimensions to a typical sample width.
        var (w, h) = post.Dimensions;
        var scale = visible == post.File || w <= 850 ? 1 : 850.0 / w;
        DisplayWidth = Math.Max(1, w * scale);
        DisplayHeight = Math.Max(1, h * scale);
    }

    public void ShowHint(string text)
    {
        Hint = text;
        var mine = text;
        _ = Task.Delay(2200).ContinueWith(_ => { if (Hint == mine) Hint = null; }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private async Task LoadImageAsync(PostImage img, string? referer, CancellationToken ct)
    {
        IsLoadingFull = true;
        try
        {
            var progress = new Progress<double>(p => { if (!ct.IsCancellationRequested) Progress = p; });
            var decoded = await _services.Images.LoadFullAsync(img.Url, referer, progress, ct);
            if (ct.IsCancellationRequested)
            {
                decoded.Animation?.Dispose();
                return;
            }
            if (decoded.Animation is { } anim)
            {
                _animation = anim;
                _frame = 0;
                Content = ViewerContent.Animation;
                Image = await Task.Run(() => anim.GetFrame(0), ct);
                if (anim.FrameCount > 1) StartFrameTimer();
            }
            else
            {
                Image = decoded.Still;
                Content = ViewerContent.Image;
            }
            var px = decoded.PixelSize;
            if (px.Width > 0 && (Math.Abs(px.Width - DisplayWidth) > 1 || Math.Abs(px.Height - DisplayHeight) > 1))
            {
                DisplayWidth = px.Width;
                DisplayHeight = px.Height;
                ResetZoomToken++;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (BooruRequestException ex) { ErrorText = ex.Message; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            ErrorText = $"Couldn't decode this image: {ex.Message}";
        }
        finally
        {
            if (!ct.IsCancellationRequested) IsLoadingFull = false;
        }
    }

    // ----- GIF / animated WebP playback -----

    private void StartFrameTimer()
    {
        if (_animation is null) return;
        _frameTimer.Interval = _animation.Durations[0];
        QueueNextFrameDecode();
        _frameTimer.Start();
    }

    private void QueueNextFrameDecode()
    {
        var anim = _animation;
        if (anim is null) return;
        var next = (_frame + 1) % anim.FrameCount;
        _frameDecode = Task.Run(() =>
        {
            try { _nextFrame = anim.GetFrame(next); }
            catch (ObjectDisposedException) { }
        });
    }

    private void OnFrameTick(object? sender, EventArgs e)
    {
        var anim = _animation;
        if (anim is null) { _frameTimer.Stop(); return; }
        if (_frameDecode is { IsCompleted: false }) return; // decoder is behind; hold this frame a little longer
        if (_nextFrame is { } f)
        {
            _frame = (_frame + 1) % anim.FrameCount;
            Image = f;
            _nextFrame = null;
        }
        _frameTimer.Interval = anim.Durations[_frame];
        QueueNextFrameDecode();
    }

    // ----- Video attachment (airspace rule) -----

    public void OnTabActiveChanged(bool active)
    {
        _tabActive = active;
        if (!active) _frameTimer.Stop();
        else if (_animation is not null && IsOpen) StartFrameTimer();
        UpdateVideoAttachment();
    }

    /// <summary>
    /// The LibVLC surface is attached only while this viewer is open, showing a video, in the selected tab, and no
    /// dialog/popup/menu is open. Otherwise the view pauses and detaches it.
    /// </summary>
    private void UpdateVideoAttachment() =>
        VideoShouldBeAttached = IsOpen && _tabActive && Content == ViewerContent.Video && VideoUrl is not null && !_services.Overlay.IsOpen;

    // ----- Preloading -----

    private void Preload(int index)
    {
        _preloadCts?.Cancel();
        var cts = _preloadCts = new CancellationTokenSource();
        var ahead = _services.Settings.Get(SettingKeys.PreloadAhead, 3);
        var behind = _services.Settings.Get(SettingKeys.PreloadBehind, 1);
        var targets = Enumerable.Range(1, ahead).Select(d => index + d)
            .Concat(Enumerable.Range(1, behind).Select(d => index - d))
            .Where(i => i >= 0 && i < _tab.Posts.Count)
            .Select(i => _tab.Posts[i].Post)
            .Select(p => (Img: p.VisibleImage(ForceOriginal), Referer: p.PostPageReferer))
            .Where(t => t.Img.Kind != MediaKind.Video) // videos stream; don't pull whole files speculatively
            .ToList();
        _ = Task.Run(async () =>
        {
            foreach (var (img, referer) in targets)
            {
                if (cts.IsCancellationRequested) return;
                try { await _services.Images.PrefetchAsync(img.Url, referer, cts.Token); }
                catch (Exception ex) when (ex is BooruRequestException or OperationCanceledException or IOException) { }
            }
        }, CancellationToken.None);
    }

    private void StopAll()
    {
        _loadCts?.Cancel();
        _frameTimer.Stop();
        _nextFrame = null;
        var anim = _animation;
        _animation = null;
        if (anim is not null)
        {
            var pending = _frameDecode;
            // Dispose after any in-flight frame decode finishes with the codec.
            _ = (pending ?? Task.CompletedTask).ContinueWith(_ => anim.Dispose(), TaskScheduler.Default);
        }
    }

    public void Dispose()
    {
        StopAll();
        _preloadCts?.Cancel();
    }
}
