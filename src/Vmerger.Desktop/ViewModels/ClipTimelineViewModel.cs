using System.Globalization;
using Vmerger.Desktop.Services;

namespace Vmerger.Desktop.ViewModels;

public sealed class ClipTimelineViewModel(Vmerger.Core.Media.SourceClip clip) : ObservableObject
{
    public string? SourceIssue
    {
        get
        {
            try
            {
                var info = new FileInfo(Path);
                if (!info.Exists) return "Source missing";
                return info.Length != SourceClip.SizeBytes || info.LastWriteTimeUtc != SourceClip.LastModified.UtcDateTime
                    ? "Source changed since import; re-import required" : null;
            }
            catch (Exception ex) { return $"Source unavailable: {ex.Message}"; }
        }
    }
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
