namespace Vmerger.Desktop.Services;

/// <summary>Clip-local range rules; UI duration and playhead are deliberately absent.</summary>
public readonly record struct PlaybackRange(double DurationSeconds, double? InSeconds, double? OutSeconds)
{
    public double StartSeconds => Math.Clamp(InSeconds ?? 0, 0, DurationSeconds);
    public double EndSeconds => Math.Clamp(OutSeconds ?? DurationSeconds, StartSeconds, DurationSeconds);
    public double PreviewStartSeconds(double previewDuration) => Math.Max(StartSeconds, EndSeconds - previewDuration);

    public bool HasReachedEnd(double actualSeconds, bool nativeEndReached) =>
        nativeEndReached || (OutSeconds is not null && actualSeconds >= EndSeconds);
}
