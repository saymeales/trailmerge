namespace Vmerger.Core.Grouping;

public sealed record ClipGroupingOptions(TimeSpan MaximumGap)
{
    public static ClipGroupingOptions Default { get; } = new(TimeSpan.FromMinutes(2));
}
