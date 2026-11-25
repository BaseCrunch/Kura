using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Kura.App.Services;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using VlcMedia = LibVLCSharp.Shared.Media;

namespace Kura.App.Controls;

/// <summary>
/// LibVLC video surface that obeys the airspace rule: the native <see cref="VideoView"/> exists in the visual tree
/// only while <see cref="ShouldAttach"/> is true. When it turns false (tab switch, dialog, popup, context menu,
/// viewer closed) playback is stopped at the current position and the surface removed, so its HWND can't paint
/// over WPF content. Re-attaching resumes from that position in the same play/pause state.
/// </summary>
public sealed class VideoHost : Grid
{
    public static readonly DependencyProperty UrlProperty = DependencyProperty.Register(
        nameof(Url), typeof(string), typeof(VideoHost), new PropertyMetadata(null, (d, _) => ((VideoHost)d).OnUrlChanged()));

    public static readonly DependencyProperty RefererProperty = DependencyProperty.Register(
        nameof(Referer), typeof(string), typeof(VideoHost), new PropertyMetadata(null));

    public static readonly DependencyProperty ShouldAttachProperty = DependencyProperty.Register(
        nameof(ShouldAttach), typeof(bool), typeof(VideoHost), new PropertyMetadata(false, (d, _) => ((VideoHost)d).Sync()));

    public static readonly DependencyProperty IsMutedProperty = DependencyProperty.Register(
        nameof(IsMuted), typeof(bool), typeof(VideoHost),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((VideoHost)d).ApplyAudio()));

    public static readonly DependencyProperty VolumeProperty = DependencyProperty.Register(
        nameof(Volume), typeof(int), typeof(VideoHost),
        new FrameworkPropertyMetadata(80, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((VideoHost)d).ApplyAudio()));

    // Read-only-ish state for the controls row (set from code, bound one-way).
    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
        nameof(Position), typeof(double), typeof(VideoHost), new PropertyMetadata(0.0));
    public static readonly DependencyProperty DurationProperty = DependencyProperty.Register(
        nameof(Duration), typeof(double), typeof(VideoHost), new PropertyMetadata(0.0));
    public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(
        nameof(IsPlaying), typeof(bool), typeof(VideoHost), new PropertyMetadata(false));
    public static readonly DependencyProperty StatusTextProperty = DependencyProperty.Register(
        nameof(StatusText), typeof(string), typeof(VideoHost), new PropertyMetadata(null));

    private readonly DispatcherTimer _positionTimer;
    private MediaPlayer? _player;
    private VlcMedia? _media;
    private VideoView? _view;
    private string? _loadedUrl;
    private long _resumeMs;
    private bool _wantPlaying = true;
    private bool _pauseOnStart;

    public VideoHost()
    {
        Background = System.Windows.Media.Brushes.Black;
        _positionTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => PollPosition(), Dispatcher);
        _positionTimer.Stop();
        Unloaded += (_, _) => Detach();
        Dispatcher.ShutdownStarted += (_, _) => DisposePlayer();
    }

    public string? Url { get => (string?)GetValue(UrlProperty); set => SetValue(UrlProperty, value); }
    public string? Referer { get => (string?)GetValue(RefererProperty); set => SetValue(RefererProperty, value); }
    public bool ShouldAttach { get => (bool)GetValue(ShouldAttachProperty); set => SetValue(ShouldAttachProperty, value); }
    public bool IsMuted { get => (bool)GetValue(IsMutedProperty); set => SetValue(IsMutedProperty, value); }
    public int Volume { get => (int)GetValue(VolumeProperty); set => SetValue(VolumeProperty, value); }
    public double Position { get => (double)GetValue(PositionProperty); private set => SetValue(PositionProperty, value); }
    public double Duration { get => (double)GetValue(DurationProperty); private set => SetValue(DurationProperty, value); }
    public bool IsPlaying { get => (bool)GetValue(IsPlayingProperty); private set => SetValue(IsPlayingProperty, value); }
    public string? StatusText { get => (string?)GetValue(StatusTextProperty); private set => SetValue(StatusTextProperty, value); }

    private void OnUrlChanged()
    {
        // A different video: forget the old resume point and start playing from the top.
        _resumeMs = 0;
        _wantPlaying = true;
        Position = 0;
        Duration = 0;
        StatusText = null;
        if (_player is not null && _loadedUrl is not null) StopPlayer();
        Sync();
    }

    private void Sync()
    {
        if (ShouldAttach && Url is not null && IsLoaded) Attach();
        else Detach();
    }

    private MediaPlayer EnsurePlayer()
    {
        if (_player is not null) return _player;
        _player = AppServices.Current.Video.CreatePlayer();
        _player.Playing += (_, _) => Dispatcher.BeginInvoke(OnPlaying);
        _player.Paused += (_, _) => Dispatcher.BeginInvoke(() => IsPlaying = false);
        _player.Stopped += (_, _) => Dispatcher.BeginInvoke(() => IsPlaying = false);
        _player.EncounteredError += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            IsPlaying = false;
            StatusText = "This video couldn't be played.";
        });
        _player.LengthChanged += (_, e) => Dispatcher.BeginInvoke(() => Duration = e.Length / 1000.0);
        return _player;
    }

    private void Attach()
    {
        var player = EnsurePlayer();
        if (_view is null)
        {
            _view = new VideoView { MediaPlayer = player };
            Children.Insert(0, _view);
        }
        if (_loadedUrl == Url && player.IsPlaying) return;

        _media?.Dispose();
        _media = AppServices.Current.Video.CreateMedia(Url!, Referer, null);
        if (_resumeMs > 0) _media.AddOption($":start-time={_resumeMs / 1000.0:0.###}".Replace(',', '.'));
        _loadedUrl = Url;
        _pauseOnStart = !_wantPlaying;
        ApplyAudio();
        StatusText = "Loading…";
        // Start once the VideoView has handed its HWND to the player, otherwise VLC opens its own window.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_view is null || _media is null || !ShouldAttach) return;
            player.Play(_media);
            _positionTimer.Start();
        });
    }

    private void OnPlaying()
    {
        StatusText = null;
        IsPlaying = true;
        ApplyAudio();
        if (_pauseOnStart)
        {
            _pauseOnStart = false;
            _player?.SetPause(true);
        }
    }

    /// <summary>Stops playback, remembering the position and play state, and removes the native surface.</summary>
    private void Detach()
    {
        if (_player is not null && _loadedUrl is not null)
        {
            if (_player.Time > 0) _resumeMs = _player.Time;
            StopPlayer();
        }
        if (_view is not null)
        {
            Children.Remove(_view);
            _view.MediaPlayer = null;
            _view.Dispose();
            _view = null;
        }
    }

    private void StopPlayer()
    {
        _positionTimer.Stop();
        _player?.Stop();
        _loadedUrl = null;
        IsPlaying = false;
    }

    public void TogglePlay()
    {
        if (_player is null || _view is null) return;
        if (_player.IsPlaying) { _player.SetPause(true); _wantPlaying = false; }
        else
        {
            _wantPlaying = true;
            if (_player.State is VLCState.Ended or VLCState.Stopped) { _resumeMs = 0; _loadedUrl = null; Attach(); }
            else _player.SetPause(false);
        }
    }

    public void SeekTo(double seconds)
    {
        if (_player is null) return;
        _player.Time = (long)(Math.Max(0, seconds) * 1000);
        Position = seconds;
    }

    public void SeekBy(double seconds) => SeekTo(Position + seconds);

    private void PollPosition()
    {
        if (_player is null) return;
        var t = _player.Time;
        if (t >= 0) Position = t / 1000.0;
    }

    private void ApplyAudio()
    {
        if (_player is null) return;
        _player.Mute = IsMuted;
        _player.Volume = Math.Clamp(Volume, 0, 100);
    }

    private void DisposePlayer()
    {
        Detach();
        _media?.Dispose();
        _media = null;
        _player?.Dispose();
        _player = null;
    }
}
