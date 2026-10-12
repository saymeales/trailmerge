using Vmerger.Core.Media;

namespace Vmerger.Core.Grouping;

public sealed record TrailSuggestion(
    string Name,
    IReadOnlyList<SourceClip> Clips,
    TimeSpan TotalDuration,
    IReadOnlyList<string> Warnings);
