using Vmerger.Core.Media;

namespace Vmerger.Core.Grouping;

public interface IClipGroupingHeuristic
{
    IReadOnlyList<TrailSuggestion> Group(IReadOnlyList<SourceClip> clips, ClipGroupingOptions options);
}
