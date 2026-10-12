using Vmerger.Core.Media;

namespace Vmerger.Media.Import;

public sealed record RecordingTimeReadResult(RecordingTimeInfo? RecordingTime, string? Diagnostic);
