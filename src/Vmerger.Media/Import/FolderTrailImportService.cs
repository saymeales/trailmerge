using Vmerger.Core.Grouping;
using Vmerger.Core.Media;

namespace Vmerger.Media.Import;

public sealed class FolderTrailImportService(
    MediaFileScanner scanner,
    SourceClipImporter importer,
    IClipGroupingHeuristic groupingHeuristic)
{
    public static FolderTrailImportService CreateDefault()
    {
        return new FolderTrailImportService(
            new MediaFileScanner(),
            SourceClipImporter.CreateDefault(),
            new GapBasedTrailGroupingHeuristic());
    }

    public async Task<FolderTrailImportResult> ImportAsync(
        string folderPath,
        ClipGroupingOptions groupingOptions,
        TimeZoneInfo fallbackTimeZone,
        CancellationToken cancellationToken = default)
    {
        var paths = scanner.FindCandidates(folderPath);
        return await ImportFilesAsync(paths, groupingOptions, fallbackTimeZone, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FolderTrailImportResult> ImportFilesAsync(
        IReadOnlyList<string> paths,
        ClipGroupingOptions groupingOptions,
        TimeZoneInfo fallbackTimeZone,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var clips = new List<SourceClip>();
        var warnings = new List<string>();
        var errors = new List<string>();

        var distinctPaths = paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var path in distinctPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await importer.ImportAsync(path, fallbackTimeZone, cancellationToken).ConfigureAwait(false);
            if (result.Succeeded && result.Clip is not null)
            {
                clips.Add(result.Clip);
                if (!string.IsNullOrWhiteSpace(result.Warning))
                {
                    warnings.Add($"{Path.GetFileName(path)}: {result.Warning}");
                }
            }
            else if (!string.IsNullOrWhiteSpace(result.Error))
            {
                errors.Add($"{Path.GetFileName(path)}: {result.Error}");
            }
        }

        var trails = groupingHeuristic.Group(clips, groupingOptions);
        return new FolderTrailImportResult(distinctPaths.Length, clips, trails, warnings, errors);
    }
}

public sealed record FolderTrailImportResult(
    int CandidateFileCount,
    IReadOnlyList<SourceClip> ImportedClips,
    IReadOnlyList<TrailSuggestion> Trails,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);
