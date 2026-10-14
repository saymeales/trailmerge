using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using Vmerger.Desktop.Services;
using Vmerger.Media.Import;

namespace Vmerger.Desktop.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable, IProjectState, ITrailEditorCallbacks
{
    private readonly MediaPrototypeSession _mediaSession = MediaPrototypeSession.CreateDefault();
    private CancellationTokenSource? _folderImportCancellation;
    private string _folderImportStatus = "Folder import: waiting";
    private bool _isDisposed;
    private readonly TemporaryProjectStore _projectStore = new(TemporaryProjectStore.DefaultPath);
    private ProjectAutosave? _autosave;
    private string _projectStatus = "Project: loading";
    private bool _isProjectLoading = true;
    private bool _isImporting;

    public bool HasExportError => Exports.HasExportError;
    public bool ExportSucceeded => Exports.ExportSucceeded;

    public string ProjectStatus { get => _projectStatus; internal set => SetProperty(ref _projectStatus, value); }
    string IProjectState.ProjectStatus { get => ProjectStatus; set => ProjectStatus = value; }
    public string? ExportDirectory { get => Exports.ExportDirectory; private set => Exports.ExportDirectory = value; }
    public bool CanEditProject => !Exports.IsExporting && !_isProjectLoading && !_isImporting;
    public bool IsExporting { get => Exports.IsExporting; private set => Exports.IsExporting = value; }
    public double ExportProgress { get => Exports.ExportProgress; private set => Exports.ExportProgress = value; }
    public string ExportProgressText { get => Exports.ExportProgressText; private set => Exports.ExportProgressText = value; }

    public async Task RestoreProjectAsync()
    {
        try
        {
            var project = await _projectStore.LoadAsync();
            if (project is not null)
            {
                foreach (var trail in project.Trails)
                {
                    var clips = trail.Clips.Select(saved => new ClipTimelineViewModel(saved.Source)
                    { StartSeconds = saved.StartSeconds, EndSeconds = saved.EndSeconds }).ToArray();
                    Trails.Add(new TrailSuggestionViewModel(trail.Name, clips));
                    foreach (var clip in clips)
                    {
                        if (clip.SourceIssue is null) _ = TrackKeyframesAsync(clip);
                        else clip.KeyframeSummary = clip.SourceIssue;
                    }
                }
                ContinueToNextClipOnEnd = project.ContinueToNextClip;
                ExportDirectory = project.ExportDirectory;
                Editor.SelectWithoutOpening(Trails.FirstOrDefault(), null);
                OnPropertyChanged(nameof(SelectedTrail));
                var issues = Trails.SelectMany(trail => trail.Clips).Count(clip => clip.SourceIssue is not null);
                ProjectStatus = issues == 0 ? "Project: restored" : $"Project: restored; {issues} missing or changed source(s)";
            }
            else ProjectStatus = "Project: autosave ready";
            _autosave = new ProjectAutosave(this, _projectStore);
            _autosave.Start();
        }
        catch (Exception ex)
        {
            ProjectStatus = $"Project: restore failed; saved JSON preserved, autosave disabled - {ex.Message}";
        }
        finally
        {
            _isProjectLoading = false;
            OnPropertyChanged(nameof(CanEditProject));
        }
    }

    public Task FlushProjectAsync() => _autosave?.FlushAsync() ?? Task.CompletedTask;

    public async Task CloseProjectAsync()
    {
        _autosave?.Dispose();
        await FlushProjectAsync();
        try { _projectStore.Delete(); }
        catch (Exception ex)
        {
            ProjectStatus = $"Project: could not clean saved project - {ex.Message}";
            _autosave?.Start();
            return;
        }
        Playback.CancelProbe();
        _folderImportCancellation?.Cancel();

        SelectedClip = null;
        _mediaSession.ClearKeyframeCache();
        SelectedTrail = null;
        Trails.Clear();
        Editor.ClearClipboard();
        SelectedTrailCount = 0;
        Playback.Reset();
        ExportDirectory = null;
        ProjectStatus = "Project: closed";
        _autosave = new ProjectAutosave(this, _projectStore);
        _autosave.Start();
    }

    public TrailEditorViewModel Editor { get; }
    public ExportViewModel Exports { get; }
    public int SelectedTrailCount { get => Editor.SelectedTrailCount; set => Editor.SelectedTrailCount = value; }
    public TrailSuggestionViewModel? SelectedTrail { get => Editor.SelectedTrail; set => Editor.SelectedTrail = value; }
    public ClipTimelineViewModel? SelectedClip { get => Editor.SelectedClip; set => Editor.SelectedClip = value; }

    public void AddTrail() => Editor.AddTrail();
    public void DeleteSelectedTrail() => Editor.DeleteSelectedTrail();
    public void MergeSelectedTrailIntoPrevious() => Editor.MergeSelectedTrailIntoPrevious();
    public void MergeSelectedTrails(IReadOnlyList<TrailSuggestionViewModel> trails) => Editor.MergeSelectedTrails(trails);
    public void SplitSelectedTrailBeforeSelectedClip() => Editor.SplitSelectedTrailBeforeSelectedClip();
    public void RemoveSelectedClip() => Editor.RemoveSelectedClip();
    public void DeleteSelection() => Editor.DeleteSelection();
    public void DuplicateSelectedClip() => Editor.DuplicateSelectedClip();
    public void SelectClip(TrailSuggestionViewModel trail, ClipTimelineViewModel clip) => Editor.SelectClip(trail, clip);
    public void SelectClip(ClipTimelineViewModel clip) => Editor.SelectClip(clip);
    public void MoveClipToTrail(ClipTimelineViewModel clip, TrailSuggestionViewModel trail) => Editor.MoveClipToTrail(clip, trail);
    public void CopySelectedClip() => Editor.CopySelectedClip();
    public void CutSelectedClip() => Editor.CutSelectedClip();
    public void PasteClipToSelectedTrail() => Editor.PasteClipToSelectedTrail();

    void ITrailEditorCallbacks.OnSelectedClipChanged(ClipTimelineViewModel? previous, ClipTimelineViewModel? current) => Playback.SelectedClipChanged(previous, current);

    void ITrailEditorCallbacks.OnClipAdded(ClipTimelineViewModel clip)
    {
        if (_mediaSession.TryGetCompletedKeyframes(clip.Path) is { } keyframes) clip.ApplyKeyframeIndex(keyframes);
        else _ = TrackKeyframesAsync(clip);
    }

    void ITrailEditorCallbacks.ReportEditStatus(string message)
    {
        Exports.ExportStatus = message;
        Exports.AppendExportOutput(message);
    }

    private void Component_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(e.PropertyName);
        if (e.PropertyName == nameof(IsExporting)) OnPropertyChanged(nameof(CanEditProject));
    }

    public Task ExportSelectedTrailAsync(string outputPath) => Exports.ExportSelectedTrailAsync(outputPath);
    public Task<bool> ExportAllTrailsAsync(string outputFolder) => Exports.ExportAllTrailsAsync(outputFolder);
    public void ClearExportOutput() => Exports.ClearExportOutput();

    public Task OpenMediaAsync(string path) => Playback.OpenMediaAsync(path);
    public void SetSelectedClipStartFromPlayhead() => Playback.SetSelectedClipStartFromPlayhead();
    public void SetSelectedClipEndFromPlayhead() => Playback.SetSelectedClipEndFromPlayhead();
    public void ClearSelectedClipRange() => Playback.ClearSelectedClipRange();
    public void SeekFromUser(double seconds) => Playback.SeekFromUser(seconds);
    public void PreviewClipFromStart(ClipTimelineViewModel clip) => Playback.PreviewClipFromStart(clip);
    public void PreviewClipTransition(ClipTimelineViewModel clip) => Playback.PreviewClipTransition(clip);
    public void TogglePlaybackFromVideoSurface() => Playback.TogglePlaybackFromVideoSurface();

    public MainWindowViewModel()
    {
        Editor = new TrailEditorViewModel(this);
        Exports = new ExportViewModel(Editor);
        Editor.PropertyChanged += Component_OnPropertyChanged;
        Exports.PropertyChanged += Component_OnPropertyChanged;
        Playback = new PlaybackViewModel(Editor);
        Playback.PropertyChanged += Component_OnPropertyChanged;
    }

    public PlaybackViewModel Playback { get; }
    public PlayerController Player => Playback.Player;

    public MediaPlayer MediaPlayer => Playback.MediaPlayer;

    public RelayCommand PlayPauseCommand => Playback.PlayPauseCommand;

    public string PlayPauseButtonText => Playback.PlayPauseButtonText;

    public bool IsPaused => Playback.IsPaused;

    public string PositionText => Playback.PositionText;

    public string DurationText => Playback.DurationText;

    public double PlaybackPositionSeconds => Playback.PlaybackPositionSeconds;

    public double PlaybackDurationSeconds => Playback.PlaybackDurationSeconds;

    public ObservableCollection<TrailSuggestionViewModel> Trails => Editor.Trails;

    public bool CanMergeSelectedTrails => Editor.CanMergeSelectedTrails;

    public string SelectedFileText { get => Playback.SelectedFileText; private set => Playback.SelectedFileText = value; }

    public string PlaybackStatus { get => Playback.PlaybackStatus; private set => Playback.PlaybackStatus = value; }

    public string MetadataStatus { get => Playback.MetadataStatus; private set => Playback.MetadataStatus = value; }

    public string KeyframeStatus { get => Playback.KeyframeStatus; private set => Playback.KeyframeStatus = value; }

    public string FolderImportStatus
    {
        get => _folderImportStatus;
        private set => SetProperty(ref _folderImportStatus, value);
    }

    public string ProbeOutput { get => Playback.ProbeOutput; private set => Playback.ProbeOutput = value; }

    public string ExportStatus { get => Exports.ExportStatus; private set => Exports.ExportStatus = value; }

    public string ExportOutput { get => Exports.ExportOutput; private set => Exports.ExportOutput = value; }

    public bool HasMedia { get => Playback.HasMedia; private set => Playback.HasMedia = value; }

    public bool IsPlaying { get => Playback.IsPlaying; private set => Playback.IsPlaying = value; }

    public bool ContinueToNextClipOnEnd { get => Playback.ContinueToNextClipOnEnd; set => Playback.ContinueToNextClipOnEnd = value; }

    public double DurationSeconds { get => Playback.DurationSeconds; private set => Playback.DurationSeconds = value; }

    public double PositionSeconds { get => Playback.PositionSeconds; private set => Playback.PositionSeconds = value; }

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

    private async Task ImportAsync(
        string operationName,
        Func<CancellationToken, Task<FolderTrailImportResult>> import)
    {
        if (!CanEditProject) return;
        _isImporting = true;
        OnPropertyChanged(nameof(CanEditProject));
        _folderImportCancellation?.Cancel();
        _folderImportCancellation?.Dispose();
        _folderImportCancellation = new CancellationTokenSource();

        Trails.Clear();
        SelectedTrail = null;
        SelectedClip = null;
        _mediaSession.ClearKeyframeCache();
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
        catch (Exception ex)
        {
            FolderImportStatus = $"{operationName}: failed - {ex.Message}";
        }
        finally
        {
            _isImporting = false;
            OnPropertyChanged(nameof(CanEditProject));
        }
    }

    private async Task TrackKeyframesAsync(ClipTimelineViewModel clip)
    {
        clip.KeyframeSummary = "Keyframes: reading";

        var index = await _mediaSession.EnsureKeyframesAsync(clip.Path).ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_isDisposed || !Trails.Any(trail => trail.Clips.Contains(clip))) return;
            clip.ApplyKeyframeIndex(index);

            Playback.ApplySelectedKeyframes(clip);
        });
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _autosave?.Dispose();
        Editor.PropertyChanged -= Component_OnPropertyChanged;
        Exports.PropertyChanged -= Component_OnPropertyChanged;

        Playback.CancelProbe();

        _folderImportCancellation?.Cancel();
        _folderImportCancellation?.Dispose();

        Playback.PropertyChanged -= Component_OnPropertyChanged;
        Playback.Dispose();
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

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }
}
