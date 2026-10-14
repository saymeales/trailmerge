using System.Globalization;
using Avalonia.Threading;
using LibVLCSharp.Shared;

namespace Vmerger.Desktop.ViewModels;

/// <summary>Owns native playback resources and dispatches transport updates to the UI.</summary>
public sealed class PlayerController : IDisposable
{
    private LibVLCSharp.Shared.Media? _currentMedia;
    private LibVLCSharp.Shared.Media? _preloadedNextMedia;
    private ClipTimelineViewModel? _preloadedNextClip;
    private readonly LibVLC _libVlc;
    private readonly DispatcherTimer _timer;
    private readonly IPlayerCallbacks _callbacks;
    private bool _isDisposed;
    private long _playbackGeneration;
    private int _pauseRequested;
    private int _inputReady;
    private double _preloadedStartSeconds;
    public bool IsAtEnd { get; private set; }
    public double CurrentTimeSeconds => MediaPlayer.Time / 1000d;

    public PlayerController(IPlayerCallbacks callbacks)
    {
        _callbacks = callbacks;
        LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC();
        MediaPlayer = new MediaPlayer(_libVlc);
        MediaPlayer.Playing += OnPlaying;
        MediaPlayer.Paused += OnPaused;
        MediaPlayer.Stopped += OnStopped;
        MediaPlayer.TimeChanged += OnTimeChanged;
        MediaPlayer.EndReached += OnEndReached;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += OnPresentationTick;
        _timer.Start();
    }

    public MediaPlayer MediaPlayer { get; }

    public LibVLCSharp.Shared.Media CreateMedia(string path)
    {
        var media = new LibVLCSharp.Shared.Media(_libVlc, path, FromType.FromPath);
        media.AddOption(":play-and-pause");
        return media;
    }

    public void Play() => MediaPlayer.Play();

    public void Play(LibVLCSharp.Shared.Media media)
    {
        Interlocked.Increment(ref _playbackGeneration);
        IsAtEnd = false;
        Volatile.Write(ref _inputReady, 0);
        Interlocked.Exchange(ref _pauseRequested, 0);
        MediaPlayer.Play(media);
    }

    public void Pause()
    {
        // SetPause is idempotent; Pause() can toggle a player that is already paused.
        if (MediaPlayer.IsPlaying) Interlocked.Exchange(ref _pauseRequested, 1);
        MediaPlayer.SetPause(true);
    }
    public void Stop()
    {
        Interlocked.Increment(ref _playbackGeneration);
        IsAtEnd = false;
        Volatile.Write(ref _inputReady, 0);
        MediaPlayer.Stop();
    }
    public void ClearMedia()
    {
        Stop();
        _currentMedia?.Dispose();
        _preloadedNextMedia?.Dispose();
        _currentMedia = null;
        _preloadedNextMedia = null;
        _preloadedNextClip = null;
    }
    public void SetPause(bool paused)
    {
        if (paused) Pause();
        else MediaPlayer.SetPause(false);
    }
    public void TogglePlayPause()
    {
        if (MediaPlayer.IsPlaying)
        {
            Pause();
            return;
        }

        _callbacks.BeforePlaybackResume();
        Play();
    }
    public void Seek(double seconds)
    {
        // Discard queued EOF/time notifications from before the seek.
        Interlocked.Increment(ref _playbackGeneration);
        IsAtEnd = false;
        MediaPlayer.Time = (long)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero);
    }

    private void OnPresentationTick(object? sender, EventArgs e) => _callbacks.UpdatePlaybackPosition();

    private void Dispatch(PlayerNotification notification, bool automaticEnd = false)
    {
        var generation = Interlocked.Read(ref _playbackGeneration);
        Dispatcher.UIThread.Post(new PendingNotification(this, notification, generation, automaticEnd).Deliver);
    }

    private void DeliverNotification(PlayerNotification notification, long generation, bool automaticEnd)
    {
        if (_isDisposed || generation != Interlocked.Read(ref _playbackGeneration)) return;
        switch (notification)
        {
            case PlayerNotification.Playing: _callbacks.OnPlayerPlaying(); break;
            case PlayerNotification.Paused:
                _callbacks.OnPlayerPaused();
                if (automaticEnd && generation == Interlocked.Read(ref _playbackGeneration)) SignalEnd();
                break;
            case PlayerNotification.Stopped: _callbacks.OnPlayerStopped(); break;
            case PlayerNotification.TimeChanged: _callbacks.OnPlayerTimeChanged(); break;
            case PlayerNotification.EndReached: SignalEnd(); break;
        }
    }

    private void OnPlaying(object? sender, EventArgs e)
    {
        Volatile.Write(ref _inputReady, 1);
        Interlocked.Exchange(ref _pauseRequested, 0);
        Dispatch(PlayerNotification.Playing);
    }

    private void OnPaused(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _inputReady) == 0) return;
        // With play-and-pause, an unrequested pause is VLC's EOF pause.
        // Capture the reason on the native event thread before posting to the UI.
        var requested = Interlocked.Exchange(ref _pauseRequested, 0) != 0;
        Dispatch(PlayerNotification.Paused, automaticEnd: !requested);
    }
    private void OnStopped(object? sender, EventArgs e) => Dispatch(PlayerNotification.Stopped);
    private void OnTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        if (Volatile.Read(ref _inputReady) != 0) Dispatch(PlayerNotification.TimeChanged);
    }
    private void OnEndReached(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _inputReady) != 0) Dispatch(PlayerNotification.EndReached);
    }

    private void SignalEnd()
    {
        if (IsAtEnd) return;
        IsAtEnd = true;
        _callbacks.OnPlayerEndReached();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _timer.Stop();
        _timer.Tick -= OnPresentationTick;
        MediaPlayer.Playing -= OnPlaying;
        MediaPlayer.Paused -= OnPaused;
        MediaPlayer.Stopped -= OnStopped;
        MediaPlayer.TimeChanged -= OnTimeChanged;
        MediaPlayer.EndReached -= OnEndReached;
        Stop();
        _currentMedia?.Dispose();
        _preloadedNextMedia?.Dispose();
        MediaPlayer.Dispose();
        _libVlc.Dispose();
    }

    public void Open(string path)
    {
        ClearMedia();
        _currentMedia = CreateMedia(path);
        Play(_currentMedia);
    }

    public void Preload(ClipTimelineViewModel? nextClip, double startSeconds)
    {
        if (ReferenceEquals(_preloadedNextClip, nextClip) && _preloadedStartSeconds == startSeconds)
        {
            return;
        }

        _preloadedNextMedia?.Dispose();
        _preloadedNextMedia = null;
        _preloadedNextClip = nextClip;
        _preloadedStartSeconds = startSeconds;

        if (nextClip is not null)
        {
            _preloadedNextMedia = CreateClipMedia(nextClip, startSeconds);
        }
    }

    private LibVLCSharp.Shared.Media CreateClipMedia(ClipTimelineViewModel clip, double startSeconds)
    {
        var media = CreateMedia(clip.Path);
        var boundedStart = Math.Clamp(startSeconds, 0, Math.Max(0, clip.Duration.TotalSeconds));
        if (boundedStart > 0)
        {
            media.AddOption($":start-time={boundedStart.ToString("0.###", CultureInfo.InvariantCulture)}");
        }

        return media;
    }

    public void OpenClip(ClipTimelineViewModel clip, double startSeconds)
    {
        if (ReferenceEquals(_preloadedNextClip, clip) && _preloadedNextMedia is not null &&
            _preloadedStartSeconds == startSeconds)
        {
            _currentMedia?.Dispose();
            _currentMedia = _preloadedNextMedia;
            _preloadedNextMedia = null;
            _preloadedNextClip = null;
        }
        else
        {
            _currentMedia?.Dispose();
            _currentMedia = CreateClipMedia(clip, startSeconds);
        }

        Play(_currentMedia);
    }

    private enum PlayerNotification { Playing, Paused, Stopped, TimeChanged, EndReached }

    private sealed record PendingNotification(PlayerController Owner, PlayerNotification Notification,
        long Generation, bool AutomaticEnd)
    {
        public void Deliver() => Owner.DeliverNotification(Notification, Generation, AutomaticEnd);
    }
}
