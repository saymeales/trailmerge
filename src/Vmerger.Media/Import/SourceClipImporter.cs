using System.Globalization;
using Vmerger.Core.Media;
using Vmerger.Media.Ffprobe;

namespace Vmerger.Media.Import;

public sealed class SourceClipImporter(
    FfprobeService ffprobeService,
    IReadOnlyList<IRecordingTimeMetadataReader> recordingTimeReaders)
{
    public static SourceClipImporter CreateDefault()
    {
        return new SourceClipImporter(new FfprobeService(), [new DjiRecordingTimeMetadataReader()]);
    }

    public async Task<SourceClipImportResult> ImportAsync(
        string path,
        TimeZoneInfo fallbackTimeZone,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                return SourceClipImportResult.Failed(path, "File does not exist.");
            }

            var metadata = await ffprobeService.ReadMetadataAsync(path, cancellationToken).ConfigureAwait(false);
            var duration = ReadDuration(metadata);
            if (duration is null || duration <= TimeSpan.Zero)
            {
                return SourceClipImportResult.Failed(path, "ffprobe did not report a valid duration.");
            }

            var recordingTime = ReadRecordingTime(metadata, file, fallbackTimeZone, out var diagnostic);
            var clip = new SourceClip(
                file.FullName,
                file.Length,
                file.LastWriteTimeUtc,
                duration.Value,
                recordingTime);

            return SourceClipImportResult.Imported(clip, diagnostic);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SourceClipImportResult.Failed(path, ex.Message);
        }
    }

    private RecordingTimeInfo ReadRecordingTime(
        FfprobeResult metadata,
        FileInfo file,
        TimeZoneInfo fallbackTimeZone,
        out string? diagnostic)
    {
        foreach (var reader in recordingTimeReaders)
        {
            var result = reader.TryRead(metadata, fallbackTimeZone);
            if (result.RecordingTime is not null)
            {
                diagnostic = result.Diagnostic;
                return result.RecordingTime;
            }
        }

        diagnostic = "No recording metadata time found; using filesystem last-write time as a low-confidence fallback.";
        return new RecordingTimeInfo(
            file.LastWriteTimeUtc,
            "filesystem:last_write_time_utc",
            RecordingTimeConfidence.Low,
            file.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture));
    }

    private static TimeSpan? ReadDuration(FfprobeResult metadata)
    {
        return double.TryParse(metadata.Format?.Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }
}

public sealed record SourceClipImportResult(SourceClip? Clip, string Path, string? Warning, string? Error)
{
    public bool Succeeded => Clip is not null && Error is null;

    public static SourceClipImportResult Imported(SourceClip clip, string? warning)
    {
        return new SourceClipImportResult(clip, clip.Path, warning, null);
    }

    public static SourceClipImportResult Failed(string path, string error)
    {
        return new SourceClipImportResult(null, path, null, error);
    }
}
