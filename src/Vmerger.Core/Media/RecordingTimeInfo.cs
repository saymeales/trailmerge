namespace Vmerger.Core.Media;

public enum RecordingTimeConfidence
{
    Unknown = 0,
    Low = 1,
    Metadata = 2,
    VerifiedVendorMetadata = 3
}

public sealed record RecordingTimeInfo(
    DateTimeOffset NormalizedStart,
    string Source,
    RecordingTimeConfidence Confidence,
    string? OriginalValue);
