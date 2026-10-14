using System.Globalization;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using Vmerger.Desktop.Services;
using Vmerger.Media.Ffprobe;

namespace Vmerger.Desktop.ViewModels;

public sealed class PlaybackViewModel : ObservableObject, IDisposable, IPlayerCallbacks
{
    private const double TransitionPreviewGap = 3;
    private readonly FfprobeService _ffprobe = new();
    private CancellationTokenSource? _probeCancellation;
    private string _selectedFileText = "No file selected";
    private string _playbackStatus = "Playback: idle";
    private string _metadataStatus = "Metadata: waiting for file";
    private string _keyframeStatus = "Keyframes: waiting for file";
    private string _probeOutput = "";
    private bool _hasMedia;
    private bool _isPlaying;
    private bool _continueToNextClipOnEnd;
    private bool _stoppedAtSelectedEnd;
    private bool _transitionPreviewActive;
    private ClipTimelineViewModel? _clipWithPendingStartTimeOption;
    private double _durationSeconds = 1;
    private double _positionSeconds;
    private int _openPlaybackGeneration;
    private int? _pauseWhenPlayingGeneration;
    private IReadOnlyList<double> _keyframeSeconds = [];
    private bool _isUpdatingPlaybackPosition;
    private bool _isDisposed;
    private readonly TrailEditorViewModel _editor;
    private ClipTimelineViewModel? SelectedClip => _editor.SelectedClip;
    private ObservableCollection<TrailSuggestionViewModel> Trails => _editor.Trails;

    public PlaybackViewModel(TrailEditorViewModel editor)
    {
        _editor = editor;
        Player = new PlayerController(this);
        PlayPauseCommand = new RelayCommand(TogglePlayPause, () => HasMedia);
    }

    public PlayerController Player { get; }
    public RelayCommand PlayPauseCommand { get; }

    private void SelectClip(ClipTimelineViewModel clip) => _editor.SelectClip(clip);

    internal void SelectedClipChanged(ClipTimelineViewModel? previous, ClipTimelineViewModel? current)
    {
        if (previous is not null) previous.PropertyChanged -= SelectedClip_OnPropertyChanged;
        OnPlaybackRangeChanged();
        if (current is not null)
        {
            current.PropertyChanged += SelectedClip_OnPropertyChanged;
            OpenClipForPlayback(current);
        }
    }

    internal void ApplySelectedKeyframes(ClipTimelineViewModel clip)
    {
        if (!ReferenceEquals(SelectedClip, clip)) return;
        _keyframeSeconds = clip.KeyframeSeconds;
        KeyframeStatus = clip.KeyframeSummary;
        OnPlaybackRangeChanged();
    }

    internal void CancelProbe() => _probeCancellation?.Cancel();

    internal void Reset()
    {
        CancelProbe();
        Player.ClearMedia();
        HasMedia = false;
        IsPlaying = false;
        _transitionPreviewActive = false;
        _clipWithPendingStartTimeOption = null;
        _keyframeSeconds = [];
        PositionSeconds = 0;
        DurationSeconds = 1;
        ProbeOutput = "";
        MetadataStatus = "Metadata: waiting for file";
        KeyframeStatus = "Keyframes: waiting for file";
        SelectedFileText = "No file selected";
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        if (SelectedClip is not null) SelectedClip.PropertyChanged -= SelectedClip_OnPropertyChanged;
        _probeCancellation?.Cancel();
        _probeCancellation?.Dispose();
        Player.Dispose();
    }
    public MediaPlayer MediaPlayer => Player.MediaPlayer;

    public string PlayPauseButtonText => IsPlaying ? "Pause" : "Play";

    public bool IsPaused => HasMedia && !IsPlaying;

    public string PositionText => FormatSeconds(PlaybackPositionSeconds);

    public string DurationText => FormatSeconds(PlaybackDurationSeconds);

    public double PlaybackPositionSeconds => Math.Clamp(PositionSeconds - GetPlaybackStartSeconds(), 0, PlaybackDurationSeconds);

    public double PlaybackDurationSeconds => Math.Max(0.001, GetPlaybackEndSeconds() - GetPlaybackStartSeconds());

    public string SelectedFileText
    {
        get => _selectedFileText;
        internal set => SetProperty(ref _selectedFileText, value);
    }

    public string PlaybackStatus
    {
        get => _playbackStatus;
        internal set => SetProperty(ref _playbackStatus, value);
    }

    public string MetadataStatus
    {
        get => _metadataStatus;
        internal set => SetProperty(ref _metadataStatus, value);
    }

    public string KeyframeStatus
    {
        get => _keyframeStatus;
        internal set => SetProperty(ref _keyframeStatus, value);
    }

    public string ProbeOutput
    {
        get => _probeOutput;
        internal set => SetProperty(ref _probeOutput, value);
    }

    public bool HasMedia
    {
        get => _hasMedia;
        internal set
        {
            if (SetProperty(ref _hasMedia, value))
            {
                PlayPauseCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(IsPaused));
            }
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        internal set
        {
            if (SetProperty(ref _isPlaying, value))
            {
                OnPropertyChanged(nameof(PlayPauseButtonText));
                OnPropertyChanged(nameof(IsPaused));
            }
        }
    }

    public bool ContinueToNextClipOnEnd
    {
        get => _continueToNextClipOnEnd;
        set => SetProperty(ref _continueToNextClipOnEnd, value);
    }

    public double DurationSeconds
    {
        get => _durationSeconds;
        internal set
        {
            if (SetProperty(ref _durationSeconds, Math.Max(1, value)))
            {
                OnPropertyChanged(nameof(DurationText));
                OnPropertyChanged(nameof(PlaybackDurationSeconds));
                OnPropertyChanged(nameof(PlaybackPositionSeconds));
            }
        }
    }

    public double PositionSeconds
    {
        get => _positionSeconds;
        internal set
        {
            if (SetProperty(ref _positionSeconds, value))
            {
                OnPropertyChanged(nameof(PositionText));
                OnPropertyChanged(nameof(PlaybackPositionSeconds));
            }
        }
    }

    public async Task OpenMediaAsync(string path)
    {
        ThrowIfDisposed();

        SelectedFileText = path;
        ProbeOutput = "";
        MetadataStatus = "Metadata: reading";
        KeyframeStatus = "Keyframes: reading";
        PlaybackStatus = "Playback: opening";
        HasMedia = true;

        OpenWithVlc(path);

        _probeCancellation?.Cancel();
        _probeCancellation?.Dispose();
        _probeCancellation = new CancellationTokenSource();

        await ProbeAsync(path, _probeCancellation.Token).ConfigureAwait(true);
    }

    public void SetSelectedClipStartFromPlayhead()
    {
        if (SelectedClip is not null)
        {
            SelectedClip.StartSeconds = SnapToKeyframe(Player.CurrentTimeSeconds);
        }
    }

    public void SetSelectedClipEndFromPlayhead()
    {
        if (SelectedClip is not null)
        {
            SelectedClip.EndSeconds = SnapToKeyframe(Player.CurrentTimeSeconds);
        }
    }

    public void ClearSelectedClipRange()
    {
        if (SelectedClip is null)
        {
            return;
        }

        SelectedClip.StartSeconds = null;
        SelectedClip.EndSeconds = null;
    }

    public void SeekFromUser(double requestedSeconds)
    {
        if (!HasMedia)
        {
            return;
        }

        var boundedSeconds = ClampToSelectedClipRange(GetPlaybackStartSeconds() + requestedSeconds);
        var snappedSeconds = ClampToSelectedClipRange(SnapToKeyframe(boundedSeconds));
        SeekToSeconds(snappedSeconds, MediaPlayer.IsPlaying);
    }

    public void PreviewClipFromStart(ClipTimelineViewModel clip)
    {
        SelectClip(clip);
        if (!HasMedia) return;
        _transitionPreviewActive = false;
        OpenClipWithVlc(clip, GetPlaybackStartSeconds(clip), stopBeforePlay: false, playAfterOpen: true);
    }

    public void PreviewClipTransition(ClipTimelineViewModel clip)
    {
        SelectClip(clip);
        Dispatcher.UIThread.Post(() => StartTransitionPreview(clip));
    }

    public void TogglePlaybackFromVideoSurface()
    {
        TogglePlayPause();
    }

    private void StartTransitionPreview(ClipTimelineViewModel clip)
    {
        if (!HasMedia || !ReferenceEquals(SelectedClip, clip))
        {
            return;
        }

        _transitionPreviewActive = true;
        _stoppedAtSelectedEnd = false;
        PreloadNextClipAfter(clip);

        var previewStart = GetPlaybackRange(clip).PreviewStartSeconds(TransitionPreviewGap);
        // start-time is applied as VLC opens the input; a seek immediately after
        // Play can otherwise be ignored or overwritten by the original start-time.
        OpenClipWithVlc(clip, previewStart, stopBeforePlay: false, playAfterOpen: true);
        PlaybackStatus = $"Playback: join preview from {FormatSeconds(previewStart)}";
    }

    private void OpenClipForPlayback(ClipTimelineViewModel clip)
    {
        ThrowIfDisposed();
        if (clip.SourceIssue is { } issue)
        {
            Player.Stop();
            HasMedia = false;
            PlaybackStatus = $"Playback: {issue}";
            return;
        }

        _probeCancellation?.Cancel();
        SelectedFileText = clip.Path;
        ProbeOutput = "";
        MetadataStatus = "Metadata: cached from import";
        _keyframeSeconds = clip.KeyframeSeconds;
        KeyframeStatus = clip.KeyframeSummary;
        DurationSeconds = clip.Duration.TotalSeconds;
        var startSeconds = GetPlaybackStartSeconds(clip);
        PositionSeconds = startSeconds;
        HasMedia = true;
        _stoppedAtSelectedEnd = false;
        OpenClipWithVlc(clip, startSeconds, stopBeforePlay: false, playAfterOpen: true);
        PreloadNextClipAfter(clip);
    }

    private void SelectedClip_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ClipTimelineViewModel.StartSeconds) or nameof(ClipTimelineViewModel.EndSeconds))
        {
            _stoppedAtSelectedEnd = false;
            OnPlaybackRangeChanged();
            EnforceSelectedClipPlaybackRange();
        }
    }

    private void OnPlaybackRangeChanged()
    {
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(PlaybackDurationSeconds));
        OnPropertyChanged(nameof(PlaybackPositionSeconds));
    }

    private void OpenWithVlc(string path)
    {
        _stoppedAtSelectedEnd = false;
        Player.Open(path);
        _clipWithPendingStartTimeOption = null;
    }

    private void OpenClipWithVlc(ClipTimelineViewModel clip, double startSeconds, bool stopBeforePlay, bool playAfterOpen)
    {
        _stoppedAtSelectedEnd = false;

        var playbackGeneration = ++_openPlaybackGeneration;
        _pauseWhenPlayingGeneration = playAfterOpen ? null : playbackGeneration;
        Player.OpenClip(clip, startSeconds);
        _clipWithPendingStartTimeOption = startSeconds > 0 ? clip : null;
        if (playAfterOpen)
        {
            PlaybackStatus = "Playback: playing";
        }
        else
        {
            PlaybackStatus = $"Playback: paused at {FormatSeconds(startSeconds)}";
        }
    }

    private void OnPlayerPlaying()
    {
        if (_isDisposed || _pauseWhenPlayingGeneration != _openPlaybackGeneration)
        {
            if (!_isDisposed)
            {
                IsPlaying = true;
                PlaybackStatus = "Playback: playing";
            }

            return;
        }

        _pauseWhenPlayingGeneration = null;
        Player.Pause();
        PositionSeconds = Math.Clamp(MediaPlayer.Time / 1000d, 0, DurationSeconds);
        PlaybackStatus = $"Playback: paused at {PositionText}";
    }

    private void OnPlayerPaused()
    {
        if (_isDisposed)
        {
            return;
        }

        IsPlaying = false;
        SetPlaybackPositionFromPlayer();
        PlaybackStatus = $"Playback: paused at {PositionText}";
    }

    private void OnPlayerStopped()
    {
        if (_isDisposed)
        {
            return;
        }

        IsPlaying = false;
        PlaybackStatus = "Playback: stopped";
    }

    private async Task ProbeAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var metadataTask = _ffprobe.ReadMetadataAsync(path, cancellationToken);
            var keyframesTask = _ffprobe.ReadKeyframesAsync(path, cancellationToken);

            var metadata = await metadataTask.ConfigureAwait(true);
            var keyframes = await keyframesTask.ConfigureAwait(true);

            cancellationToken.ThrowIfCancellationRequested();

            _keyframeSeconds = keyframes
                .Select(ReadFrameSeconds)
                .Where(static time => time is not null)
                .Select(static time => time!.Value)
                .Distinct()
                .Order()
                .ToArray();

            DurationSeconds = ReadDurationSeconds(metadata) ?? Math.Max(1, MediaPlayer.Length / 1000d);
            MetadataStatus = "Metadata: loaded";
            KeyframeStatus = $"Keyframes: {_keyframeSeconds.Count}";
            ProbeOutput = FormatProbeSummary(metadata, keyframes, _keyframeSeconds);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            MetadataStatus = "Metadata: failed";
            KeyframeStatus = "Keyframes: failed";
            ProbeOutput = ex.Message;
        }
    }

    private void TogglePlayPause()
    {
        if (!HasMedia)
        {
            return;
        }

        Player.TogglePlayPause();
    }

    private void SeekToSeconds(double seconds, bool playAfterSeek = true)
    {
        if (!HasMedia)
        {
            return;
        }

        var boundedSeconds = Math.Clamp(seconds, 0, DurationSeconds);
        _stoppedAtSelectedEnd = false;
        _clipWithPendingStartTimeOption = null;
        Player.Pause();
        Player.Seek(boundedSeconds);
        SetPlaybackPositionFromPlayer();
        if (playAfterSeek)
        {
            Player.Play();
        }
    }

    private double SnapToKeyframe(double requestedSeconds)
    {
        if (_keyframeSeconds.Count == 0)
        {
            return Math.Clamp(requestedSeconds, 0, DurationSeconds);
        }

        var boundedSeconds = Math.Clamp(requestedSeconds, 0, DurationSeconds);
        var best = _keyframeSeconds[0];
        var bestDistance = Math.Abs(best - boundedSeconds);

        foreach (var keyframe in _keyframeSeconds.Skip(1))
        {
            var distance = Math.Abs(keyframe - boundedSeconds);
            if (distance < bestDistance)
            {
                best = keyframe;
                bestDistance = distance;
            }
            else if (Math.Abs(distance - bestDistance) < 0.000_001 && keyframe < best)
            {
                best = keyframe;
            }
        }

        return best;
    }

    private void UpdatePlaybackPosition()
    {
        if (!HasMedia || _isUpdatingPlaybackPosition)
        {
            return;
        }

        SetPlaybackPositionFromPlayer();

        if (MediaPlayer.Length > 0)
        {
            DurationSeconds = MediaPlayer.Length / 1000d;
        }

    }

    private void SetPlaybackPositionFromPlayer()
    {
        _isUpdatingPlaybackPosition = true;
        try
        {
            PositionSeconds = Math.Clamp(MediaPlayer.Time / 1000d, 0, DurationSeconds);
        }
        finally
        {
            _isUpdatingPlaybackPosition = false;
        }
    }

    private void EnforceSelectedClipPlaybackRange()
    {
        if (!HasMedia || _stoppedAtSelectedEnd || SelectedClip is not { } clip)
        {
            return;
        }

        var start = GetPlaybackStartSeconds(clip);
        var end = GetPlaybackEndSeconds(clip);
        var actualSeconds = Player.CurrentTimeSeconds;
        if (actualSeconds < 0) return; // VLC has not reported a valid clock yet.

        if (ReferenceEquals(_clipWithPendingStartTimeOption, clip) && actualSeconds >= start)
        {
            _clipWithPendingStartTimeOption = null;
        }

        if (actualSeconds < start)
        {
            if (ReferenceEquals(_clipWithPendingStartTimeOption, clip))
            {
                return;
            }

            SeekToSeconds(start, MediaPlayer.IsPlaying);
            return;
        }

        // Full-file playback ends on VLC's terminal event, not a comparison to
        // metadata duration: the final clock sample can be below that duration.
        if (GetPlaybackRange(clip).HasReachedEnd(actualSeconds, nativeEndReached: false))
        {
            HandleSelectedClipEnd(clip, end);
        }
    }

    private void OnPlayerEndReached()
    {
        if (_isDisposed || !HasMedia || SelectedClip is not { } clip) return;
        HandleSelectedClipEnd(clip, GetPlaybackEndSeconds(clip));
    }

    private void HandleSelectedClipEnd(ClipTimelineViewModel clip, double end)
    {
        if (_stoppedAtSelectedEnd)
        {
            return;
        }

        _stoppedAtSelectedEnd = true;

        if ((ContinueToNextClipOnEnd || _transitionPreviewActive) && TryPlayNextClip(clip))
        {
            return;
        }

        _transitionPreviewActive = false;
        Player.Pause();
        PositionSeconds = end;
        PlaybackStatus = $"Playback: reached selected end at {FormatSeconds(end)}";
    }

    private bool TryPlayNextClip(ClipTimelineViewModel clip)
    {
        if (TryGetNextClip(clip) is not { } nextClip)
        {
            return false;
        }

        var trail = FindTrailForClip(nextClip);
        if (trail is null)
        {
            return false;
        }

        PlayNextClipFromPreload(trail, nextClip);
        _transitionPreviewActive = false;
        return true;
    }

    private void PlayNextClipFromPreload(TrailSuggestionViewModel trail, ClipTimelineViewModel nextClip)
    {
        if (_editor.SelectedClip is not null)
        {
            _editor.SelectedClip.PropertyChanged -= SelectedClip_OnPropertyChanged;
        }

        _editor.SelectWithoutOpening(trail, nextClip);
        nextClip.PropertyChanged += SelectedClip_OnPropertyChanged;

        SelectedFileText = nextClip.Path;
        ProbeOutput = "";
        MetadataStatus = "Metadata: cached from import";
        _keyframeSeconds = nextClip.KeyframeSeconds;
        KeyframeStatus = nextClip.KeyframeSummary;
        DurationSeconds = nextClip.Duration.TotalSeconds;

        var startSeconds = GetPlaybackStartSeconds(nextClip);
        PositionSeconds = startSeconds;
        HasMedia = true;
        _stoppedAtSelectedEnd = false;
        OnPlaybackRangeChanged();

        OpenClipWithVlc(nextClip, startSeconds, stopBeforePlay: false, playAfterOpen: true);
        PreloadNextClipAfter(nextClip);
    }

    private ClipTimelineViewModel? TryGetNextClip(ClipTimelineViewModel clip)
    {
        var trail = FindTrailForClip(clip);
        if (trail is null)
        {
            return null;
        }

        var index = trail.Clips.IndexOf(clip);
        return index >= 0 && index + 1 < trail.Clips.Count
            ? trail.Clips[index + 1]
            : null;
    }

    private void PreloadNextClipAfter(ClipTimelineViewModel clip)
    {
        var nextClip = TryGetNextClip(clip);
        Player.Preload(nextClip, nextClip is null ? 0 : GetPlaybackStartSeconds(nextClip));
    }

    private double ClampToSelectedClipRange(double seconds)
    {
        if (SelectedClip is not { } clip)
        {
            return Math.Clamp(seconds, 0, DurationSeconds);
        }

        return Math.Clamp(seconds, GetPlaybackStartSeconds(clip), GetPlaybackEndSeconds(clip));
    }

    private double GetPlaybackStartSeconds()
    {
        return SelectedClip is { } clip ? GetPlaybackStartSeconds(clip) : 0;
    }

    private double GetPlaybackEndSeconds()
    {
        return SelectedClip is { } clip ? GetPlaybackEndSeconds(clip) : DurationSeconds;
    }

    private double GetPlaybackStartSeconds(ClipTimelineViewModel clip)
    {
        return GetPlaybackRange(clip).StartSeconds;
    }

    private double GetPlaybackEndSeconds(ClipTimelineViewModel clip)
    {
        return GetPlaybackRange(clip).EndSeconds;
    }

    private static double? ReadDurationSeconds(FfprobeResult metadata)
    {
        return TryParseSeconds(metadata.Format?.Duration);
    }

    private static double? ReadFrameSeconds(FfprobeFrame frame)
    {
        return TryParseSeconds(frame.BestEffortTimestampTime)
            ?? TryParseSeconds(frame.PtsTime)
            ?? TryParseSeconds(frame.PacketPtsTime);
    }

    private static double? TryParseSeconds(string? value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : null;
    }

    private static string FormatProbeSummary(
        FfprobeResult metadata,
        IReadOnlyList<FfprobeFrame> keyframes,
        IReadOnlyList<double> keyframeSeconds)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Format");
        builder.AppendLine($"  Name: {metadata.Format?.FormatName ?? "unknown"}");
        builder.AppendLine($"  Duration: {metadata.Format?.Duration ?? "unknown"}");
        builder.AppendLine($"  Size: {metadata.Format?.Size ?? "unknown"}");
        builder.AppendLine();
        builder.AppendLine("Streams");

        foreach (var stream in metadata.Streams)
        {
            builder.AppendLine($"  #{stream.Index}: {stream.CodecType ?? "?"} / {stream.CodecName ?? "?"}");

            if (stream.Width is not null || stream.Height is not null)
            {
                builder.AppendLine($"      Video: {stream.Width}x{stream.Height}, avg fps {stream.AverageFrameRate ?? "unknown"}");
            }

            if (!string.IsNullOrWhiteSpace(stream.Duration))
            {
                builder.AppendLine($"      Duration: {stream.Duration}");
            }

            if (!string.IsNullOrWhiteSpace(stream.TimeBase))
            {
                builder.AppendLine($"      Time base: {stream.TimeBase}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("First keyframes");

        foreach (var seconds in keyframeSeconds.Take(20))
        {
            builder.AppendLine($"  {FormatSeconds(seconds)}");
        }

        if (keyframeSeconds.Count == 0)
        {
            foreach (var frame in keyframes.Take(20))
            {
                var time = frame.BestEffortTimestampTime ?? frame.PtsTime ?? frame.PacketPtsTime ?? "unknown";
                builder.AppendLine($"  stream {frame.StreamIndex}: {time} ({frame.PictureType ?? "?"})");
            }
        }

        return builder.ToString();
    }

    private static string FormatTime(long milliseconds)
    {
        return TimeSpan.FromMilliseconds(milliseconds).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
    }

    private static string FormatSeconds(double seconds)
    {
        return TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
    }

    private TrailSuggestionViewModel? FindTrailForClip(ClipTimelineViewModel clip)
    {
        return Trails.FirstOrDefault(trail => trail.Clips.Contains(clip));
    }

    void IPlayerCallbacks.BeforePlaybackResume()
    {
        if (SelectedClip is not null && (Player.IsAtEnd || _stoppedAtSelectedEnd ||
            Player.CurrentTimeSeconds >= GetPlaybackEndSeconds() - 0.000_001))
        {
            SeekToSeconds(GetPlaybackStartSeconds(), playAfterSeek: false);
        }
        _stoppedAtSelectedEnd = false;
    }

    void IPlayerCallbacks.UpdatePlaybackPosition() => UpdatePlaybackPosition();
    void IPlayerCallbacks.OnPlayerPlaying() => OnPlayerPlaying();
    void IPlayerCallbacks.OnPlayerPaused() => OnPlayerPaused();
    void IPlayerCallbacks.OnPlayerStopped() => OnPlayerStopped();
    void IPlayerCallbacks.OnPlayerTimeChanged() => EnforceSelectedClipPlaybackRange();
    void IPlayerCallbacks.OnPlayerEndReached() => OnPlayerEndReached();

    private static PlaybackRange GetPlaybackRange(ClipTimelineViewModel clip) =>
        new(clip.Duration.TotalSeconds, clip.StartSeconds, clip.EndSeconds);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }
}
