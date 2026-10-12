using System.Globalization;
using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using Vmerger.Core.Grouping;
using Vmerger.Desktop.Services;
using Vmerger.Media.Export;
using Vmerger.Media.Ffprobe;
using Vmerger.Media.Import;

namespace Vmerger.Desktop.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly LibVLC _libVlc;
    private readonly FfprobeService _ffprobe = new();
    private readonly StreamCopyExportService _exportService = new();
    private readonly MediaPrototypeSession _mediaSession = MediaPrototypeSession.CreateDefault();
    private readonly DispatcherTimer _playbackTimer;
    private CancellationTokenSource? _probeCancellation;
    private CancellationTokenSource? _folderImportCancellation;
    private LibVLCSharp.Shared.Media? _currentMedia;
    private string _selectedFileText = "No file selected";
    private string _playbackStatus = "Playback: idle";
    private string _metadataStatus = "Metadata: waiting for file";
    private string _keyframeStatus = "Keyframes: waiting for file";
    private string _folderImportStatus = "Folder import: waiting";
    private string _probeOutput = "";
    private string _exportStatus = "Export: waiting";
    private string _exportOutput = "";
    private bool _hasMedia;
    private bool _isPlaying;
    private TrailSuggestionViewModel? _selectedTrail;
    private ClipTimelineViewModel? _selectedClip;
    private ClipTimelineViewModel? _clipClipboard;
    private bool _clipboardCut;
    private double _durationSeconds = 1;
    private double _positionSeconds;
    private IReadOnlyList<double> _keyframeSeconds = [];
    private bool _isUpdatingPlaybackPosition;
    private bool _isDisposed;

    public MainWindowViewModel()
    {
        LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC();
        MediaPlayer = new MediaPlayer(_libVlc);

        PlayPauseCommand = new RelayCommand(TogglePlayPause, () => HasMedia);

        _playbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _playbackTimer.Tick += (_, _) => UpdatePlaybackPosition();
        _playbackTimer.Start();
    }

    public MediaPlayer MediaPlayer { get; }

    public RelayCommand PlayPauseCommand { get; }

    public string PlayPauseButtonText => IsPlaying ? "Pause" : "Play";

    public string PositionText => FormatSeconds(PositionSeconds);

    public string DurationText => FormatSeconds(DurationSeconds);

    public ObservableCollection<TrailSuggestionViewModel> Trails { get; } = [];

    public TrailSuggestionViewModel? SelectedTrail
    {
        get => _selectedTrail;
        set
        {
            if (SetProperty(ref _selectedTrail, value))
            {
                SelectedClip = null;
            }
        }
    }

    public ClipTimelineViewModel? SelectedClip
    {
        get => _selectedClip;
        set
        {
            if (SetProperty(ref _selectedClip, value) && value is not null)
            {
                OpenClipForPlayback(value);
            }
        }
    }

    public string SelectedFileText
    {
        get => _selectedFileText;
        private set => SetProperty(ref _selectedFileText, value);
    }

    public string PlaybackStatus
    {
        get => _playbackStatus;
        private set => SetProperty(ref _playbackStatus, value);
    }

    public string MetadataStatus
    {
        get => _metadataStatus;
        private set => SetProperty(ref _metadataStatus, value);
    }

    public string KeyframeStatus
    {
        get => _keyframeStatus;
        private set => SetProperty(ref _keyframeStatus, value);
    }

    public string FolderImportStatus
    {
        get => _folderImportStatus;
        private set => SetProperty(ref _folderImportStatus, value);
    }

    public string ProbeOutput
    {
        get => _probeOutput;
        private set => SetProperty(ref _probeOutput, value);
    }

    public string ExportStatus
    {
        get => _exportStatus;
        private set => SetProperty(ref _exportStatus, value);
    }

    public string ExportOutput
    {
        get => _exportOutput;
        private set => SetProperty(ref _exportOutput, value);
    }

    public bool HasMedia
    {
        get => _hasMedia;
        private set
        {
            if (SetProperty(ref _hasMedia, value))
            {
                PlayPauseCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (SetProperty(ref _isPlaying, value))
            {
                OnPropertyChanged(nameof(PlayPauseButtonText));
            }
        }
    }

    public double DurationSeconds
    {
        get => _durationSeconds;
        private set
        {
            if (SetProperty(ref _durationSeconds, Math.Max(1, value)))
            {
                OnPropertyChanged(nameof(DurationText));
            }
        }
    }

    public double PositionSeconds
    {
        get => _positionSeconds;
        private set
        {
            if (SetProperty(ref _positionSeconds, value))
            {
                OnPropertyChanged(nameof(PositionText));
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

    public async Task ImportFolderAsync(string folderPath)
    {
        ThrowIfDisposed();

        await ImportAsync(
            "Folder import",
            cancellationToken => _mediaSession.ImportFolderAsync(folderPath, cancellationToken)).ConfigureAwait(true);
    }

    public async Task ImportFilesAsync(IReadOnlyList<string> paths)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(paths);

        await ImportAsync(
            "File import",
            cancellationToken => _mediaSession.ImportFilesAsync(paths, cancellationToken)).ConfigureAwait(true);
    }

    public void AddTrail()
    {
        var trail = new TrailSuggestionViewModel($"Trail {Trails.Count + 1}", []);
        Trails.Add(trail);
        SelectedTrail = trail;
    }

    public void DeleteSelectedTrail()
    {
        if (SelectedTrail is null)
        {
            return;
        }

        var index = Trails.IndexOf(SelectedTrail);
        Trails.Remove(SelectedTrail);
        SelectedTrail = Trails.Count == 0 ? null : Trails[Math.Clamp(index, 0, Trails.Count - 1)];
    }

    public void MergeSelectedTrailIntoPrevious()
    {
        if (SelectedTrail is null)
        {
            return;
        }

        var index = Trails.IndexOf(SelectedTrail);
        if (index <= 0)
        {
            return;
        }

        var previous = Trails[index - 1];
        foreach (var clip in SelectedTrail.Clips)
        {
            previous.Clips.Add(clip);
        }

        Trails.RemoveAt(index);
        previous.RefreshSummary();
        SelectedTrail = previous;
    }

    public void SplitSelectedTrailBeforeSelectedClip()
    {
        if (SelectedTrail is null || SelectedClip is null)
        {
            return;
        }

        var clipIndex = SelectedTrail.Clips.IndexOf(SelectedClip);
        if (clipIndex <= 0)
        {
            return;
        }

        var movedClips = SelectedTrail.Clips.Skip(clipIndex).ToArray();
        foreach (var clip in movedClips)
        {
            SelectedTrail.Clips.Remove(clip);
        }

        SelectedTrail.RefreshSummary();

        var newTrail = new TrailSuggestionViewModel($"{SelectedTrail.Name} split", movedClips);
        var trailIndex = Trails.IndexOf(SelectedTrail);
        Trails.Insert(trailIndex + 1, newTrail);
        SelectedTrail = newTrail;
        SelectedClip = movedClips[0];
    }

    public void RemoveSelectedClip()
    {
        if (SelectedClip is null || FindTrailForClip(SelectedClip) is not { } owner)
        {
            return;
        }

        var index = owner.Clips.IndexOf(SelectedClip);
        owner.Clips.Remove(SelectedClip);
        owner.RefreshSummary();
        SelectedTrail = owner;
        SelectedClip = owner.Clips.Count == 0 ? null : owner.Clips[Math.Clamp(index, 0, owner.Clips.Count - 1)];
    }

    public void DeleteSelection()
    {
        if (SelectedClip is not null)
        {
            RemoveSelectedClip();
            return;
        }

        DeleteSelectedTrail();
    }

    public void DuplicateSelectedClip()
    {
        if (SelectedTrail is null || SelectedClip is null)
        {
            return;
        }

        var index = SelectedTrail.Clips.IndexOf(SelectedClip);
        var duplicate = CreateDuplicateClip(SelectedClip);

        SelectedTrail.Clips.Insert(index + 1, duplicate);
        SelectedTrail.RefreshSummary();
        SelectedClip = duplicate;
    }

    public void SelectClip(TrailSuggestionViewModel trail, ClipTimelineViewModel clip)
    {
        SelectedTrail = trail;
        SelectedClip = clip;
    }

    public void SelectClip(ClipTimelineViewModel clip)
    {
        if (FindTrailForClip(clip) is { } trail)
        {
            SelectClip(trail, clip);
        }
    }

    public void MoveClipToTrail(ClipTimelineViewModel clip, TrailSuggestionViewModel targetTrail)
    {
        MoveClipToTrail(clip, targetTrail, targetTrail.Clips.Count);
    }

    private void MoveClipToTrail(ClipTimelineViewModel clip, TrailSuggestionViewModel targetTrail, int targetIndex)
    {
        if (FindTrailForClip(clip) is not { } sourceTrail || ReferenceEquals(sourceTrail, targetTrail))
        {
            SelectedTrail = targetTrail;
            SelectedClip = clip;
            return;
        }

        sourceTrail.Clips.Remove(clip);
        sourceTrail.RefreshSummary();
        targetTrail.Clips.Insert(Math.Clamp(targetIndex, 0, targetTrail.Clips.Count), clip);
        targetTrail.RefreshSummary();
        SelectedTrail = targetTrail;
        SelectedClip = clip;
    }

    private int GetPasteIndex(TrailSuggestionViewModel targetTrail)
    {
        if (_clipClipboard is not null)
        {
            var sourceIndex = targetTrail.Clips.IndexOf(_clipClipboard);
            if (sourceIndex >= 0)
            {
                return sourceIndex + 1;
            }
        }

        if (SelectedClip is not null && ReferenceEquals(FindTrailForClip(SelectedClip), targetTrail))
        {
            return targetTrail.Clips.IndexOf(SelectedClip) + 1;
        }

        return targetTrail.Clips.Count;
    }

    public void CopySelectedClip()
    {
        if (SelectedClip is null)
        {
            return;
        }

        _clipClipboard = SelectedClip;
        _clipboardCut = false;
    }

    public void CutSelectedClip()
    {
        if (SelectedClip is null)
        {
            return;
        }

        _clipClipboard = SelectedClip;
        _clipboardCut = true;
    }

    public void PasteClipToSelectedTrail()
    {
        if (_clipClipboard is null || SelectedTrail is null)
        {
            return;
        }

        if (_clipboardCut)
        {
            MoveClipToTrail(_clipClipboard, SelectedTrail, GetPasteIndex(SelectedTrail));
            _clipClipboard = null;
            _clipboardCut = false;
            return;
        }

        var duplicate = CreateDuplicateClip(_clipClipboard);
        SelectedTrail.Clips.Insert(GetPasteIndex(SelectedTrail), duplicate);
        SelectedTrail.RefreshSummary();
        SelectedClip = duplicate;
    }

    public void SetSelectedClipStartFromPlayhead()
    {
        if (SelectedClip is not null)
        {
            SelectedClip.StartSeconds = SnapToKeyframe(PositionSeconds);
        }
    }

    public void SetSelectedClipEndFromPlayhead()
    {
        if (SelectedClip is not null)
        {
            SelectedClip.EndSeconds = SnapToKeyframe(PositionSeconds);
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

    public async Task ExportSelectedTrailAsync(string outputPath)
    {
        if (SelectedTrail is null)
        {
            ExportStatus = "Export: no selected trail";
            AppendExportOutput(ExportStatus);
            return;
        }

        foreach (var clip in SelectedTrail.Clips)
        {
            if (clip.StartSeconds is { } start && clip.EndSeconds is { } end && start >= end)
            {
                ExportStatus = $"Export: invalid range in {clip.FileName}";
                AppendExportOutput(ExportStatus);
                return;
            }
        }

        var ranges = SelectedTrail.Clips
            .Select(static clip => new ExportClipRange(clip.Path, clip.StartSeconds, clip.EndSeconds))
            .ToArray();

        ExportStatus = "Export: running";
        ExportOutput = "";
        var progress = new Progress<string>(AppendExportOutput);

        try
        {
            await _exportService.ExportAsync(ranges, outputPath, progress).ConfigureAwait(true);
            ExportStatus = $"Export: completed {outputPath}";
            AppendExportOutput(ExportStatus);
        }
        catch (Exception ex)
        {
            ExportStatus = $"Export: failed - {ex.Message}";
            AppendExportOutput(ExportStatus);
        }
    }

    public void ClearExportOutput()
    {
        ExportOutput = "";
    }

    private ClipTimelineViewModel CreateDuplicateClip(ClipTimelineViewModel source)
    {
        var duplicate = new ClipTimelineViewModel(source.SourceClip);
        if (_mediaSession.TryGetCompletedKeyframes(duplicate.Path) is { } keyframes)
        {
            duplicate.ApplyKeyframeIndex(keyframes);
        }
        else
        {
            _ = TrackKeyframesAsync(duplicate);
        }

        return duplicate;
    }

    private void AppendExportOutput(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var next = ExportOutput.Length == 0
            ? line
            : $"{ExportOutput}{Environment.NewLine}{line}";

        const int maxLength = 80_000;
        ExportOutput = next.Length <= maxLength
            ? next
            : next[^maxLength..];
    }

    private TrailSuggestionViewModel? FindTrailForClip(ClipTimelineViewModel clip)
    {
        return Trails.FirstOrDefault(trail => trail.Clips.Contains(clip));
    }

    private async Task ImportAsync(
        string operationName,
        Func<CancellationToken, Task<FolderTrailImportResult>> import)
    {
        _folderImportCancellation?.Cancel();
        _folderImportCancellation?.Dispose();
        _folderImportCancellation = new CancellationTokenSource();

        Trails.Clear();
        SelectedTrail = null;
        SelectedClip = null;
        FolderImportStatus = $"{operationName}: scanning and indexing keyframes";

        try
        {
            var result = await import(_folderImportCancellation.Token).ConfigureAwait(true);

            foreach (var trail in result.Trails)
            {
                Trails.Add(new TrailSuggestionViewModel(trail));
            }

            _mediaSession.StartKeyframeIndexing(result.ImportedClips);
            foreach (var clip in Trails.SelectMany(static trail => trail.Clips))
            {
                _ = TrackKeyframesAsync(clip);
            }

            SelectedTrail = Trails.FirstOrDefault();
            FolderImportStatus =
                $"{operationName}: {result.ImportedClips.Count}/{result.CandidateFileCount} files, {result.Trails.Count} trail groups, keyframe indexing running";

            if (result.Warnings.Count > 0 || result.Errors.Count > 0)
            {
                ProbeOutput = FormatFolderImportDiagnostics(result);
            }
        }
        catch (OperationCanceledException)
        {
            FolderImportStatus = $"{operationName}: canceled";
        }
    }

    public void SeekFromUser(double requestedSeconds)
    {
        if (!HasMedia)
        {
            return;
        }

        SeekToSeconds(SnapToKeyframe(requestedSeconds));
    }

    private void OpenClipForPlayback(ClipTimelineViewModel clip)
    {
        ThrowIfDisposed();

        _probeCancellation?.Cancel();
        SelectedFileText = clip.Path;
        ProbeOutput = "";
        MetadataStatus = "Metadata: cached from import";
        _keyframeSeconds = clip.KeyframeSeconds;
        KeyframeStatus = clip.KeyframeSummary;
        DurationSeconds = clip.Duration.TotalSeconds;
        PositionSeconds = 0;
        HasMedia = true;
        OpenWithVlc(clip.Path);
    }

    private async Task TrackKeyframesAsync(ClipTimelineViewModel clip)
    {
        clip.KeyframeSummary = "Keyframes: reading";

        var index = await _mediaSession.EnsureKeyframesAsync(clip.Path).ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            clip.ApplyKeyframeIndex(index);

            if (ReferenceEquals(SelectedClip, clip))
            {
                _keyframeSeconds = clip.KeyframeSeconds;
                KeyframeStatus = clip.KeyframeSummary;
            }
        });
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _playbackTimer.Stop();
        _probeCancellation?.Cancel();
        _probeCancellation?.Dispose();
        _folderImportCancellation?.Cancel();
        _folderImportCancellation?.Dispose();
        StopAndReleasePlayback();
        MediaPlayer.Dispose();
        _libVlc.Dispose();
    }

    private void OpenWithVlc(string path)
    {
        MediaPlayer.Stop();
        _currentMedia?.Dispose();
        _currentMedia = new LibVLCSharp.Shared.Media(_libVlc, path, FromType.FromPath);
        MediaPlayer.Play(_currentMedia);
        IsPlaying = true;
        PlaybackStatus = "Playback: playing";
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

        if (MediaPlayer.IsPlaying)
        {
            MediaPlayer.Pause();
            IsPlaying = false;
            PlaybackStatus = $"Playback: paused at {FormatTime(MediaPlayer.Time)}";
        }
        else
        {
            MediaPlayer.Play();
            IsPlaying = true;
            PlaybackStatus = "Playback: playing";
        }
    }

    private void SeekToSeconds(double seconds)
    {
        if (!HasMedia)
        {
            return;
        }

        var boundedSeconds = Math.Clamp(seconds, 0, DurationSeconds);
        MediaPlayer.Pause();
        IsPlaying = false;
        MediaPlayer.Time = (long)Math.Round(boundedSeconds * 1000, MidpointRounding.AwayFromZero);
        SetPlaybackPositionFromPlayer();
        MediaPlayer.Play();
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

        if (IsPlaying != MediaPlayer.IsPlaying)
        {
            IsPlaying = MediaPlayer.IsPlaying;
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

    private void StopAndReleasePlayback()
    {
        MediaPlayer.Stop();
        _currentMedia?.Dispose();
        _currentMedia = null;
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

    private static string FormatFolderImportDiagnostics(FolderTrailImportResult result)
    {
        var builder = new StringBuilder();

        if (result.Warnings.Count > 0)
        {
            builder.AppendLine("Import warnings");
            foreach (var warning in result.Warnings)
            {
                builder.AppendLine($"  {warning}");
            }
        }

        if (result.Errors.Count > 0)
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.AppendLine("Import errors");
            foreach (var error in result.Errors)
            {
                builder.AppendLine($"  {error}");
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

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }
}

public sealed class TrailSuggestionViewModel : ObservableObject
{
    private string _name;
    private string _summary;

    public TrailSuggestionViewModel(TrailSuggestion trail)
        : this(trail.Name, trail.Clips.Select(static clip => new ClipTimelineViewModel(clip)))
    {
    }

    public TrailSuggestionViewModel(string name, IEnumerable<ClipTimelineViewModel> clips)
    {
        _name = name;
        Clips = new ObservableCollection<ClipTimelineViewModel>(clips);
        _summary = "";
        RefreshSummary();
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public ObservableCollection<ClipTimelineViewModel> Clips { get; }

    public void RefreshSummary()
    {
        var totalDuration = TimeSpan.FromTicks(Clips.Sum(static clip => clip.Duration.Ticks));
        Summary = Clips.Count == 0
            ? "0 clips"
            : $"{Clips.Count} clip(s), {totalDuration:hh\\:mm\\:ss}, first {Clips[0].StartText}";
    }
}

public sealed class ClipTimelineViewModel(Vmerger.Core.Media.SourceClip clip) : ObservableObject
{
    private string _keyframeSummary = "Keyframes: queued";
    private IReadOnlyList<double> _keyframeSeconds = [];
    private double? _startSeconds;
    private double? _endSeconds;

    public Vmerger.Core.Media.SourceClip SourceClip { get; } = clip;

    public string Path { get; } = clip.Path;

    public TimeSpan Duration { get; } = clip.Duration;

    public IReadOnlyList<double> KeyframeSeconds
    {
        get => _keyframeSeconds;
        private set => SetProperty(ref _keyframeSeconds, value);
    }

    public string FileName { get; } = System.IO.Path.GetFileName(clip.Path);

    public string StartText { get; } = clip.RecordingTime.NormalizedStart.LocalDateTime.ToString(
        "yyyy-MM-dd HH:mm:ss",
        CultureInfo.InvariantCulture);

    public string DurationText { get; } = clip.Duration.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

    public double? StartSeconds
    {
        get => _startSeconds;
        set
        {
            if (SetProperty(ref _startSeconds, value))
            {
                OnPropertyChanged(nameof(RangeText));
            }
        }
    }

    public double? EndSeconds
    {
        get => _endSeconds;
        set
        {
            if (SetProperty(ref _endSeconds, value))
            {
                OnPropertyChanged(nameof(RangeText));
            }
        }
    }

    public string RangeText
    {
        get
        {
            var start = StartSeconds is null ? "start" : FormatSeconds(StartSeconds.Value);
            var end = EndSeconds is null ? "end" : FormatSeconds(EndSeconds.Value);
            return $"{start} - {end}";
        }
    }

    public string TimeSourceText { get; } = clip.RecordingTime.Source;

    public string KeyframeSummary
    {
        get => _keyframeSummary;
        set => SetProperty(ref _keyframeSummary, value);
    }

    public void ApplyKeyframeIndex(ClipKeyframeIndex keyframeIndex)
    {
        KeyframeSeconds = keyframeIndex.KeyframeSeconds;
        KeyframeSummary = keyframeIndex.Error is null
            ? $"Keyframes: {KeyframeSeconds.Count}"
            : $"Keyframes: failed";
    }

    private static string FormatSeconds(double seconds)
    {
        return TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
    }
}
