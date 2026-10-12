using Vmerger.Media.Ffprobe;

namespace Vmerger.Media.Import;

public interface IRecordingTimeMetadataReader
{
    string Name { get; }

    RecordingTimeReadResult TryRead(FfprobeResult metadata, TimeZoneInfo fallbackTimeZone);
}
