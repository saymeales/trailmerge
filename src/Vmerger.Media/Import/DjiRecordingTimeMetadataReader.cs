using System.Globalization;
using Vmerger.Core.Media;
using Vmerger.Media.Ffprobe;

namespace Vmerger.Media.Import;

public sealed class DjiRecordingTimeMetadataReader : IRecordingTimeMetadataReader
{
    private static readonly string[] PreferredTags =
    [
        "creation_time",
        "com.apple.quicktime.creationdate",
        "date",
        "DateTimeOriginal"
    ];

    public string Name => "DJI";

    public RecordingTimeReadResult TryRead(FfprobeResult metadata, TimeZoneInfo fallbackTimeZone)
    {
        foreach (var (scope, tags) in EnumerateTagScopes(metadata))
        {
            foreach (var tagName in PreferredTags)
            {
                if (TryGetTag(tags, tagName, out var value) && TryParseTimestamp(value, fallbackTimeZone, out var timestamp))
                {
                    var confidence = HasExplicitOffset(value)
                        ? RecordingTimeConfidence.VerifiedVendorMetadata
                        : RecordingTimeConfidence.Metadata;

                    return new RecordingTimeReadResult(
                        new RecordingTimeInfo(timestamp, $"{Name}:{scope}:{tagName}", confidence, value),
                        null);
                }
            }
        }

        return new RecordingTimeReadResult(null, "No supported DJI recording-time metadata tag was found.");
    }

    private static IEnumerable<(string Scope, IReadOnlyDictionary<string, string>? Tags)> EnumerateTagScopes(FfprobeResult metadata)
    {
        yield return ("format", metadata.Format?.Tags);

        foreach (var stream in metadata.Streams.OrderBy(static stream => stream.Index))
        {
            yield return ($"stream:{stream.Index}", stream.Tags);
        }
    }

    private static bool TryGetTag(IReadOnlyDictionary<string, string>? tags, string tagName, out string value)
    {
        value = "";

        if (tags is null)
        {
            return false;
        }

        foreach (var (key, tagValue) in tags)
        {
            if (string.Equals(key, tagName, StringComparison.OrdinalIgnoreCase))
            {
                value = tagValue;
                return !string.IsNullOrWhiteSpace(value);
            }
        }

        return false;
    }

    private static bool TryParseTimestamp(string value, TimeZoneInfo fallbackTimeZone, out DateTimeOffset timestamp)
    {
        timestamp = default;

        if (HasExplicitOffset(value)
            && DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out timestamp))
        {
            return true;
        }

        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateTime))
        {
            return false;
        }

        var unspecified = DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified);
        timestamp = new DateTimeOffset(unspecified, fallbackTimeZone.GetUtcOffset(unspecified));
        return true;
    }

    private static bool HasExplicitOffset(string value)
    {
        return value.EndsWith('Z')
            || value.Contains('+', StringComparison.Ordinal)
            || value.LastIndexOf('-', Math.Max(0, value.Length - 6), Math.Min(6, value.Length)) >= 0;
    }
}
