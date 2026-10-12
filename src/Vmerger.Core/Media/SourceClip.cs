namespace Vmerger.Core.Media;

public sealed record SourceClip(
    string Path,
    long SizeBytes,
    DateTimeOffset LastModified,
    TimeSpan Duration,
    RecordingTimeInfo RecordingTime);
