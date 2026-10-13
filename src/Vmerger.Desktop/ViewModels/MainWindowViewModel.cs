using System.Globalization;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private const double TransitionPreviewGap = 3;
    private readonly LibVLC _libVlc;
    private readonly FfprobeService _ffprobe = new();
    private readonly StreamCopyExportService _exportService = new();
    private readonly MediaPrototypeSession _mediaSession = MediaPrototypeSession.CreateDefault();
    private readonly DispatcherTimer _playbackTimer;
    private CancellationTokenSource? _probeCancellation;
    private CancellationTokenSource? _folderImportCancellation;
    private LibVLCSharp.Shared.Media? _currentMedia;
    private LibVLCSharp.Shared.Media? _preloadedNextMedia;
    private ClipTimelineViewModel? _preloadedNextClip;
    private string _selectedFileText = "No file selected";
    private string _playbackStatus = "Playback: idle";
    private string _metadataStatus = "Metadata: waiting for file";
    private string _keyframeStatus = "Keyframes: waiting for file";
    private string _folderImportStatus = "Folder import: waiting";
    private string _probeOutput = "";
    private string _exportStatus = "Export: waiting";
    private string _exportOutput = "";
    private int _selectedTrailCount;
    private bool _hasMedia;
    private bool _isPlaying;
    private bool _continueToNextClipOnEnd;
    private bool _stoppedAtSelectedEnd;
    private bool _transitionPreviewActive;
    private ClipTimelineViewModel? _clipWithPendingStartTimeOption;
    private TrailSuggestionViewModel? _selectedTrail;
    private ClipTimelineViewModel? _selectedClip;
    private ClipTimelineViewModel? _clipClipboard;
    private bool _clipboardCut;
    private double _durationSeconds = 1;
    private double _positionSeconds;
    private int _openPlaybackGeneration;
    private int? _pauseWhenPlayingGeneration;
    private IReadOnlyList<double> _keyframeSeconds = [];
    private bool _isUpdatingPlaybackPosition;
    private bool _isDisposed;

    public MainWindowViewModel()
    {
        LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC();
        MediaPlayer = new MediaPlayer(_libVlc);
        MediaPlayer.Playing += MediaPlayer_OnPlaying;
        MediaPlayer.Paused += MediaPlayer_OnPaused;
        MediaPlayer.Stopped += MediaPlayer_OnStopped;

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

    public bool IsPaused => HasMedia && !IsPlaying;

    public string PositionText => FormatSeconds(PlaybackPositionSeconds);

    public string DurationText => FormatSeconds(PlaybackDurationSeconds);

    public double PlaybackPositionSeconds => Math.Clamp(PositionSeconds - GetPlaybackStartSeconds(), 0, PlaybackDurationSeconds);

    public double PlaybackDurationSeconds => Math.Max(0.001, GetPlaybackEndSeconds() - GetPlaybackStartSeconds());

    public ObservableCollection<TrailSuggestionViewModel> Trails { get; } = [];

    public int SelectedTrailCount
    {
        get => _selectedTrailCount;
        set
        {
            if (SetProperty(ref _selectedTrailCount, value))
            {
                OnPropertyChanged(nameof(CanMergeSelectedTrails));
            }
        }
    }

    public bool CanMergeSelectedTrails => SelectedTrailCount >= 2;

    public TrailSuggestionViewModel? SelectedTrail
    {
        get => _selectedTrail;
        set
        {
            if (SetProperty(ref _selectedTrail, value))
            {
                SelectedClip = value?.Clips.FirstOrDefault();
            }
        }
    }

    public ClipTimelineViewModel? SelectedClip
    {
        get => _selectedClip;
        set
        {
            if (ReferenceEquals(_selectedClip, value))
            {
                return;
            }

            if (_selectedClip is not null)
            {
                _selectedClip.PropertyChanged -= SelectedClip_OnPropertyChanged;
            }

            _selectedClip = value;
            OnPropertyChanged();
            OnPlaybackRangeChanged();

            if (value is not null)
            {
                value.PropertyChanged += SelectedClip_OnPropertyChanged;
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
                OnPropertyChanged(nameof(IsPaused));
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
        private set
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
        private set
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

    public void MergeSelectedTrails(IReadOnlyList<TrailSuggestionViewModel> selectedTrails)
    {
        ArgumentNullException.ThrowIfNull(selectedTrails);

        var orderedTrails = selectedTrails
            .Distinct()
            .Select(trail => new { Trail = trail, Index = Trails.IndexOf(trail) })
            .Where(static item => item.Index >= 0)
            .OrderBy(static item => item.Index)
            .Select(static item => item.Trail)
            .ToArray();

        if (orderedTrails.Length < 2)
        {
            ExportStatus = "Merge: select at least two trails";
            AppendExportOutput(ExportStatus);
            return;
        }

        var target = orderedTrails[0];
        foreach (var source in orderedTrails.Skip(1))
        {
            foreach (var clip in source.Clips)
            {
                target.Clips.Add(clip);
            }
        }

        foreach (var source in orderedTrails.Skip(1).Reverse())
        {
            Trails.Remove(source);
        }

        target.RefreshSummary();
        SelectedTrail = target;
        SelectedClip = target.Clips.FirstOrDefault();
        ExportStatus = $"Merge: merged {orderedTrails.Length} trails into {target.Name}";
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

        await ExportTrailAsync(SelectedTrail, outputPath, clearOutput: true).ConfigureAwait(true);
    }

    public async Task ExportAllTrailsAsync(string outputFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);

        if (Trails.Count == 0)
        {
            ExportStatus = "Export: no trails";
            AppendExportOutput(ExportStatus);
            return;
        }

        ExportStatus = "Export: running all trails";
        ExportOutput = "";

        var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var succeeded = 0;
        var failed = 0;

        foreach (var trail in Trails)
        {
            var outputPath = CreateUniqueOutputPath(outputFolder, trail.Name, reservedPaths);
            reservedPaths.Add(outputPath);
            if (await ExportTrailAsync(trail, outputPath, clearOutput: false).ConfigureAwait(true))
            {
                succeeded++;
            }
            else
            {
                failed++;
            }
        }

        ExportStatus = failed == 0
            ? $"Export: completed {succeeded}/{Trails.Count} trails"
            : $"Export: completed {succeeded}/{Trails.Count}, failed {failed}";
        AppendExportOutput(ExportStatus);
    }

    private async Task<bool> ExportTrailAsync(TrailSuggestionViewModel trail, string outputPath, bool clearOutput)
    {
        foreach (var clip in trail.Clips)
        {
            if (clip.StartSeconds is { } start && clip.EndSeconds is { } end && start >= end)
            {
                ExportStatus = $"Export: invalid range in {clip.FileName}";
                AppendExportOutput(ExportStatus);
                return false;
            }
        }

        var ranges = trail.Clips
            .Select(static clip => new ExportClipRange(clip.Path, clip.StartSeconds, clip.EndSeconds))
            .ToArray();

        ExportStatus = $"Export: running {trail.Name}";
        if (clearOutput)
        {
            ExportOutput = "";
        }

        var progress = new Progress<string>(AppendExportOutput);

        try
        {
            await _exportService.ExportAsync(ranges, outputPath, progress).ConfigureAwait(true);
            ExportStatus = $"Export: completed {outputPath}";
            AppendExportOutput(ExportStatus);
            return true;
        }
        catch (Exception ex)
        {
            ExportStatus = $"Export: failed {trail.Name} - {ex.Message}";
            AppendExportOutput(ExportStatus);
            return false;
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

    private static string CreateUniqueOutputPath(
        string outputFolder,
        string trailName,
        ISet<string> reservedPaths)
    {
        Directory.CreateDirectory(outputFolder);

        var baseName = SanitizeFileName(trailName);
        var candidate = Path.Combine(outputFolder, $"{baseName}.mp4");
        var suffix = 2;

        while (reservedPaths.Contains(candidate) || File.Exists(candidate))
        {
            candidate = Path.Combine(outputFolder, $"{baseName}-{suffix}.mp4");
            suffix++;
        }

        return candidate;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);

        foreach (var character in name.Trim())
        {
            builder.Append(invalid.Contains(character) ? '_' : character);
        }

        var sanitized = builder.ToString().Trim(' ', '.');
        return string.IsNullOrWhiteSpace(sanitized) ? "trail" : sanitized;
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

        var boundedSeconds = ClampToSelectedClipRange(GetPlaybackStartSeconds() + requestedSeconds);
        var snappedSeconds = ClampToSelectedClipRange(SnapToKeyframe(boundedSeconds));
        SeekToSeconds(snappedSeconds, MediaPlayer.IsPlaying);
    }

    public void PreviewClipFromStart(ClipTimelineViewModel clip)
    {
        SelectClip(clip);
        _transitionPreviewActive = false;
        SeekToSeconds(GetPlaybackStartSeconds(clip));
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
        if (!ReferenceEquals(SelectedClip, clip))
        {
            return;
        }

        _transitionPreviewActive = true;
        _stoppedAtSelectedEnd = false;
        PreloadNextClipAfter(clip);

        var start = GetPlaybackStartSeconds(clip);
        var end = GetPlaybackEndSeconds(clip);
        var previewStart = Math.Max(start, end - TransitionPreviewGap);
        SeekToSeconds(previewStart);
        PlaybackStatus = $"Playback: join preview from {FormatSeconds(previewStart)}";
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
        var startSeconds = GetPlaybackStartSeconds(clip);
        PositionSeconds = startSeconds;
        HasMedia = true;
        _stoppedAtSelectedEnd = false;
        OpenClipWithVlc(clip, startSeconds, stopBeforePlay: false, playAfterOpen: true);
        PreloadNextClipAfter(clip);
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
                OnPlaybackRangeChanged();
            }
        });
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
        MediaPlayer.Playing -= MediaPlayer_OnPlaying;
        MediaPlayer.Paused -= MediaPlayer_OnPaused;
        MediaPlayer.Stopped -= MediaPlayer_OnStopped;
        MediaPlayer.Dispose();
        _libVlc.Dispose();
    }

    private void OpenWithVlc(string path)
    {
        _stoppedAtSelectedEnd = false;
        MediaPlayer.Stop();
        _currentMedia?.Dispose();
        _preloadedNextMedia?.Dispose();
        _preloadedNextMedia = null;
        _preloadedNextClip = null;
        _currentMedia = new LibVLCSharp.Shared.Media(_libVlc, path, FromType.FromPath);
        MediaPlayer.Play(_currentMedia);
        _clipWithPendingStartTimeOption = null;
    }

    private void OpenClipWithVlc(ClipTimelineViewModel clip, double startSeconds, bool stopBeforePlay, bool playAfterOpen)
    {
        _stoppedAtSelectedEnd = false;

        if (!MediaPlayer.IsPlaying)
        {
            MediaPlayer.SetPause(false);
        }

        if (ReferenceEquals(_preloadedNextClip, clip) && _preloadedNextMedia is not null)
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

        var playbackGeneration = ++_openPlaybackGeneration;
        _pauseWhenPlayingGeneration = playAfterOpen ? null : playbackGeneration;
        MediaPlayer.Play(_currentMedia);
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

    private void MediaPlayer_OnPlaying(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
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
            MediaPlayer.Pause();
            PositionSeconds = Math.Clamp(MediaPlayer.Time / 1000d, 0, DurationSeconds);
            PlaybackStatus = $"Playback: paused at {PositionText}";
        });
    }

    private void MediaPlayer_OnPaused(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_isDisposed)
            {
                return;
            }

            IsPlaying = false;
            SetPlaybackPositionFromPlayer();
            PlaybackStatus = $"Playback: paused at {PositionText}";
        });
    }

    private void MediaPlayer_OnStopped(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_isDisposed)
            {
                return;
            }

            IsPlaying = false;
            PlaybackStatus = "Playback: stopped";
        });
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
        }
        else
        {
            if (SelectedClip is not null && PositionSeconds >= GetPlaybackEndSeconds() - 0.000_001)
            {
                SeekToSeconds(GetPlaybackStartSeconds(), playAfterSeek: false);
            }

            _stoppedAtSelectedEnd = false;
            MediaPlayer.Play();
        }
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
        MediaPlayer.Pause();
        MediaPlayer.Time = (long)Math.Round(boundedSeconds * 1000, MidpointRounding.AwayFromZero);
        SetPlaybackPositionFromPlayer();
        if (playAfterSeek)
        {
            MediaPlayer.Play();
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

        EnforceSelectedClipPlaybackRange();

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
        if (SelectedClip is not { } clip)
        {
            return;
        }

        var start = GetPlaybackStartSeconds(clip);
        var end = GetPlaybackEndSeconds(clip);

        if (ReferenceEquals(_clipWithPendingStartTimeOption, clip) && PositionSeconds >= start)
        {
            _clipWithPendingStartTimeOption = null;
        }

        if (PositionSeconds < start)
        {
            if (ReferenceEquals(_clipWithPendingStartTimeOption, clip))
            {
                return;
            }

            SeekToSeconds(start, MediaPlayer.IsPlaying);
            return;
        }

        if (PositionSeconds >= end)
        {
            HandleSelectedClipEnd(clip, end);
        }
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
        MediaPlayer.Pause();
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
        if (_selectedClip is not null)
        {
            _selectedClip.PropertyChanged -= SelectedClip_OnPropertyChanged;
        }

        _selectedTrail = trail;
        _selectedClip = nextClip;
        nextClip.PropertyChanged += SelectedClip_OnPropertyChanged;
        OnPropertyChanged(nameof(SelectedTrail));
        OnPropertyChanged(nameof(SelectedClip));

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
        if (ReferenceEquals(_preloadedNextClip, nextClip))
        {
            return;
        }

        _preloadedNextMedia?.Dispose();
        _preloadedNextMedia = null;
        _preloadedNextClip = nextClip;

        if (nextClip is not null)
        {
            _preloadedNextMedia = CreateClipMedia(nextClip, GetPlaybackStartSeconds(nextClip));
        }
    }

    private LibVLCSharp.Shared.Media CreateClipMedia(ClipTimelineViewModel clip, double startSeconds)
    {
        var media = new LibVLCSharp.Shared.Media(_libVlc, clip.Path, FromType.FromPath);
        var boundedStart = Math.Clamp(startSeconds, 0, Math.Max(0, clip.Duration.TotalSeconds));
        if (boundedStart > 0)
        {
            media.AddOption($":start-time={boundedStart.ToString("0.###", CultureInfo.InvariantCulture)}");
        }

        return media;
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
        return Math.Clamp(clip.StartSeconds ?? 0, 0, DurationSeconds);
    }

    private double GetPlaybackEndSeconds(ClipTimelineViewModel clip)
    {
        var end = clip.EndSeconds ?? DurationSeconds;
        return Math.Clamp(end, GetPlaybackStartSeconds(clip), DurationSeconds);
    }

    private void StopAndReleasePlayback()
    {
        MediaPlayer.Stop();
        _currentMedia?.Dispose();
        _preloadedNextMedia?.Dispose();
        _currentMedia = null;
        _preloadedNextMedia = null;
        _preloadedNextClip = null;
        _clipWithPendingStartTimeOption = null;
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

    public double DurationSeconds { get; } = Math.Max(clip.Duration.TotalSeconds, 0.001);

    public double? StartSeconds
    {
        get => _startSeconds;
        set
        {
            if (SetProperty(ref _startSeconds, value))
            {
                OnPropertyChanged(nameof(RangeText));
                OnPropertyChanged(nameof(StartSliderSeconds));
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
                OnPropertyChanged(nameof(EndSliderSeconds));
            }
        }
    }

    public double StartSliderSeconds
    {
        get => StartSeconds ?? 0;
        set => StartSeconds = SnapStartBoundary(value);
    }

    public double EndSliderSeconds
    {
        get => EndSeconds ?? DurationSeconds;
        set => EndSeconds = SnapEndBoundary(value);
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
        OnPropertyChanged(nameof(StartSliderSeconds));
        OnPropertyChanged(nameof(EndSliderSeconds));
    }

    private double SnapStartBoundary(double requestedSeconds)
    {
        var end = EndSeconds ?? DurationSeconds;
        var maximumStart = Math.Max(0, end - 0.001);
        var snapped = SnapToBoundary(requestedSeconds, includeEndOfFile: false);

        if (snapped < end)
        {
            return snapped;
        }

        var previous = GetBoundarySeconds(includeEndOfFile: false)
            .Where(boundary => boundary < end)
            .DefaultIfEmpty(0)
            .Max();

        return Math.Clamp(previous, 0, maximumStart);
    }

    private double SnapEndBoundary(double requestedSeconds)
    {
        var start = StartSeconds ?? 0;
        var minimumEnd = Math.Min(DurationSeconds, start + 0.001);
        var snapped = SnapToBoundary(requestedSeconds, includeEndOfFile: true);

        if (snapped > start)
        {
            return snapped;
        }

        var next = GetBoundarySeconds(includeEndOfFile: true)
            .Where(boundary => boundary > start)
            .DefaultIfEmpty(DurationSeconds)
            .Min();

        return Math.Clamp(next, minimumEnd, DurationSeconds);
    }

    private double SnapToBoundary(double requestedSeconds, bool includeEndOfFile)
    {
        var boundedSeconds = Math.Clamp(requestedSeconds, 0, DurationSeconds);
        var boundaries = GetBoundarySeconds(includeEndOfFile).ToArray();
        if (boundaries.Length == 0)
        {
            return boundedSeconds;
        }

        var best = boundaries[0];
        var bestDistance = Math.Abs(best - boundedSeconds);

        foreach (var boundary in boundaries.Skip(1))
        {
            var distance = Math.Abs(boundary - boundedSeconds);
            if (distance < bestDistance)
            {
                best = boundary;
                bestDistance = distance;
            }
            else if (Math.Abs(distance - bestDistance) < 0.000_001 && boundary < best)
            {
                best = boundary;
            }
        }

        return best;
    }

    private IEnumerable<double> GetBoundarySeconds(bool includeEndOfFile)
    {
        yield return 0;

        foreach (var keyframeSecond in KeyframeSeconds)
        {
            var bounded = Math.Clamp(keyframeSecond, 0, DurationSeconds);
            if (bounded > 0 && bounded < DurationSeconds)
            {
                yield return bounded;
            }
        }

        if (includeEndOfFile)
        {
            yield return DurationSeconds;
        }
    }

    private static string FormatSeconds(double seconds)
    {
        return TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
    }
}
