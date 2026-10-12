using Vmerger.Core.Grouping;
using Vmerger.Core.Media;
using Vmerger.Media.Ffprobe;
using Vmerger.Media.Import;
using System.Collections.Concurrent;

namespace Vmerger.Desktop.Services;

public sealed class MediaPrototypeSession(FolderTrailImportService folderTrailImportService)
{
    private readonly FfprobeService _ffprobe = new();
    private readonly ConcurrentDictionary<string, Task<ClipKeyframeIndex>> _keyframeTasks = new(StringComparer.OrdinalIgnoreCase);

    public static MediaPrototypeSession CreateDefault()
    {
        return new MediaPrototypeSession(FolderTrailImportService.CreateDefault());
    }

    public Task<FolderTrailImportResult> ImportFolderAsync(
        string folderPath,
        CancellationToken cancellationToken = default)
    {
        return folderTrailImportService.ImportAsync(
            folderPath,
            ClipGroupingOptions.Default,
            TimeZoneInfo.Local,
            cancellationToken);
    }

    public Task<FolderTrailImportResult> ImportFilesAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        return folderTrailImportService.ImportFilesAsync(
            paths,
            ClipGroupingOptions.Default,
            TimeZoneInfo.Local,
            cancellationToken);
    }

    public Task<ClipKeyframeIndex> EnsureKeyframesAsync(string path, CancellationToken cancellationToken = default)
    {
        return _keyframeTasks.GetOrAdd(path, static (key, state) => state.ReadKeyframesAsync(key, CancellationToken.None), this);
    }

    public ClipKeyframeIndex? TryGetCompletedKeyframes(string path)
    {
        return _keyframeTasks.TryGetValue(path, out var task) && task.Status == TaskStatus.RanToCompletion
            ? task.Result
            : null;
    }

    public void StartKeyframeIndexing(IReadOnlyList<SourceClip> clips)
    {
        var paths = clips
            .Select(static clip => clip.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _ = Task.Run(async () =>
        {
            var maxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

            await Parallel.ForEachAsync(
                paths,
                new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism },
                (path, _) =>
                {
                    EnsureKeyframesAsync(path);
                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);
        });
    }

    private async Task<ClipKeyframeIndex> ReadKeyframesAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var packets = await _ffprobe.ReadKeyframePacketsAsync(path, cancellationToken).ConfigureAwait(false);
            var seconds = packets
                .Select(ReadPacketSeconds)
                .Where(static time => time is not null)
                .Select(static time => time!.Value)
                .Distinct()
                .Order()
                .ToArray();

            return new ClipKeyframeIndex(path, seconds, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ClipKeyframeIndex(path, [], ex.Message);
        }
    }

    private static double? ReadPacketSeconds(FfprobePacket packet)
    {
        return TryParseSeconds(packet.PtsTime)
            ?? TryParseSeconds(packet.DtsTime);
    }

    private static double? TryParseSeconds(string? value)
    {
        return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : null;
    }
}

public sealed record ClipKeyframeIndex(string Path, IReadOnlyList<double> KeyframeSeconds, string? Error);
