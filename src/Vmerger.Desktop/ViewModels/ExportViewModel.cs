using System.Text;
using Vmerger.Media.Export;

namespace Vmerger.Desktop.ViewModels;

public sealed class ExportViewModel(TrailEditorViewModel editor, StreamCopyExportService? exportService = null) : ObservableObject
{
    private readonly StreamCopyExportService _exportService = exportService ?? new();
    private string _exportStatus = "Waiting";
    private string _exportOutput = "";
    private string? _exportDirectory;
    private bool _isExporting;
    private double _exportProgress;
    private string _exportProgressText = "";
    private bool _hasExportError;
    private bool _exportSucceeded;
    public bool HasExportError => _hasExportError;
    public bool ExportSucceeded => _exportSucceeded;

    private void SetExportOutcome(bool? succeeded)
    {
        _hasExportError = succeeded == false;
        _exportSucceeded = succeeded == true;
        OnPropertyChanged(nameof(HasExportError));
        OnPropertyChanged(nameof(ExportSucceeded));
    }

    public string? ExportDirectory { get => _exportDirectory; internal set => SetProperty(ref _exportDirectory, value); }

    public bool IsExporting
    {
        get => _isExporting;
        internal set { SetProperty(ref _isExporting, value); }
    }

    public double ExportProgress { get => _exportProgress; internal set => SetProperty(ref _exportProgress, value); }

    public string ExportProgressText { get => _exportProgressText; internal set => SetProperty(ref _exportProgressText, value); }

    public string ExportStatus
    {
        get => _exportStatus;
        internal set
        {
            if (SetProperty(ref _exportStatus, value)) SetExportOutcome(null);
        }
    }

    public string ExportOutput
    {
        get => _exportOutput;
        internal set => SetProperty(ref _exportOutput, value);
    }

    public async Task ExportSelectedTrailAsync(string outputPath)
    {
        if (IsExporting) return;
        if (editor.SelectedTrail is null)
        {
            ExportStatus = "Export: no selected trail";
            SetExportOutcome(false);
            AppendExportOutput(ExportStatus);
            return;
        }

        ExportDirectory = Path.GetDirectoryName(outputPath);
        IsExporting = true;
        SetExportOutcome(null);
        ExportProgress = 0;
        try
        {
            SetExportOutcome(await ExportTrailAsync(editor.SelectedTrail, outputPath, clearOutput: true).ConfigureAwait(true));
        }
        finally { IsExporting = false; }
    }

    public async Task<bool> ExportAllTrailsAsync(string outputFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
        if (IsExporting) return false;

        if (editor.Trails.Count == 0)
        {
            ExportStatus = "No trails";
            SetExportOutcome(false);
            AppendExportOutput(ExportStatus);
            return false;
        }

        ExportStatus = "Running all trails";
        SetExportOutcome(null);
        ExportOutput = "";
        ExportDirectory = outputFolder;
        IsExporting = true;
        ExportProgress = 0;
        var trails = editor.Trails.ToArray();

        var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var succeeded = 0;
        var failed = 0;

        try
        {
            foreach (var trail in trails)
            {
                var outputPath = CreateUniqueOutputPath(outputFolder, trail.Name, reservedPaths);
                reservedPaths.Add(outputPath);
                if (await ExportTrailAsync(trail, outputPath, clearOutput: false, succeeded + failed, trails.Length).ConfigureAwait(true))
                {
                    succeeded++;
                }
                else
                {
                    failed++;
                }
            }

            ExportStatus = failed == 0
                ? $"Completed {succeeded}/{editor.Trails.Count} trails"
                : $"Completed {succeeded}/{editor.Trails.Count}, failed {failed}";
            AppendExportOutput(ExportStatus);
            SetExportOutcome(failed == 0);
            return failed == 0;
        }
        catch (Exception ex)
        {
            ExportStatus = $"Failed - {ex.Message}";
            SetExportOutcome(false);
            AppendExportOutput(ExportStatus);
            return false;
        }
        finally { IsExporting = false; }
    }

    private async Task<bool> ExportTrailAsync(TrailSuggestionViewModel trail, string outputPath, bool clearOutput,
        int completedTrails = 0, int totalTrails = 1)
    {
        foreach (var clip in trail.Clips)
        {
            if (clip.SourceIssue is { } issue)
            {
                ExportStatus = $"{clip.FileName} - {issue}";
                AppendExportOutput(ExportStatus);
                return false;
            }
            var start = clip.StartSeconds ?? 0;
            var end = clip.EndSeconds ?? clip.Duration.TotalSeconds;
            if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || start >= end || end > clip.Duration.TotalSeconds)
            {
                ExportStatus = $"Invalid range in {clip.FileName}";
                AppendExportOutput(ExportStatus);
                return false;
            }
        }

        var ranges = trail.Clips
            .Select(static clip => new ExportClipRange(clip.Path, clip.StartSeconds, clip.EndSeconds, clip.Duration.TotalSeconds))
            .ToArray();

        ExportStatus = $"Running {trail.Name}";
        if (clearOutput)
        {
            ExportOutput = "";
        }

        var progress = new Progress<string>(AppendExportOutput);
        var visibleProgress = new Progress<ExportProgressUpdate>(update =>
        {
            ExportProgress = 100 * (completedTrails + update.Fraction) / totalTrails;
            ExportProgressText = $"{ExportProgress:0}% - Trail {completedTrails + 1}/{totalTrails}: {trail.Name} - {update.Stage}";
        });

        try
        {
            await _exportService.ExportAsync(ranges, outputPath, progress, detailedProgress: visibleProgress).ConfigureAwait(true);
            ExportProgress = 100d * (completedTrails + 1) / totalTrails;
            ExportStatus = $"Completed {outputPath}";
            AppendExportOutput(ExportStatus);
            return true;
        }
        catch (Exception ex)
        {
            ExportStatus = $"Failed {trail.Name} - {ex.Message}";
            AppendExportOutput(ExportStatus);
            return false;
        }
    }

    public void ClearExportOutput()
    {
        ExportOutput = "";
    }

    internal void AppendExportOutput(string line)
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

}
