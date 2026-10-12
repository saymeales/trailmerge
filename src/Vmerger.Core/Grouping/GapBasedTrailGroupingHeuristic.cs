using Vmerger.Core.Media;

namespace Vmerger.Core.Grouping;

public sealed class GapBasedTrailGroupingHeuristic : IClipGroupingHeuristic
{
    public IReadOnlyList<TrailSuggestion> Group(IReadOnlyList<SourceClip> clips, ClipGroupingOptions options)
    {
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentNullException.ThrowIfNull(options);

        var orderedClips = clips
            .OrderBy(static clip => clip.RecordingTime.NormalizedStart)
            .ThenBy(static clip => clip.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (orderedClips.Length == 0)
        {
            return [];
        }

        var groups = new List<TrailSuggestion>();
        var current = new List<SourceClip> { orderedClips[0] };
        var warnings = new List<string>();

        for (var index = 1; index < orderedClips.Length; index++)
        {
            var previous = current[^1];
            var next = orderedClips[index];
            var previousEnd = previous.RecordingTime.NormalizedStart + previous.Duration;
            var gap = next.RecordingTime.NormalizedStart - previousEnd;

            if (gap >= TimeSpan.Zero && gap <= options.MaximumGap)
            {
                current.Add(next);
                continue;
            }

            if (gap < TimeSpan.Zero)
            {
                warnings.Add($"Overlap or uncertain order before {Path.GetFileName(next.Path)}: gap {gap}.");
            }

            groups.Add(CreateSuggestion(groups.Count + 1, current, warnings));
            current = [next];
            warnings = [];
        }

        groups.Add(CreateSuggestion(groups.Count + 1, current, warnings));
        return groups;
    }

    private static TrailSuggestion CreateSuggestion(int index, IReadOnlyList<SourceClip> clips, IReadOnlyList<string> warnings)
    {
        var totalDuration = TimeSpan.FromTicks(clips.Sum(static clip => clip.Duration.Ticks));
        var first = clips[0].RecordingTime.NormalizedStart.LocalDateTime;
        return new TrailSuggestion(
            $"Trail {index} - {first:yyyy-MM-dd HH-mm}",
            clips.ToArray(),
            totalDuration,
            warnings.ToArray());
    }
}
