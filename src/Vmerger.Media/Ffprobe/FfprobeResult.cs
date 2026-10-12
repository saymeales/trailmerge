using System.Text.Json.Serialization;

namespace Vmerger.Media.Ffprobe;

public sealed record FfprobeResult(
    [property: JsonPropertyName("format")] FfprobeFormat? Format,
    [property: JsonPropertyName("streams")] IReadOnlyList<FfprobeStream> Streams,
    [property: JsonPropertyName("frames")] IReadOnlyList<FfprobeFrame>? Frames,
    [property: JsonPropertyName("packets")] IReadOnlyList<FfprobePacket>? Packets);

public sealed record FfprobeFormat(
    [property: JsonPropertyName("filename")] string? Filename,
    [property: JsonPropertyName("format_name")] string? FormatName,
    [property: JsonPropertyName("duration")] string? Duration,
    [property: JsonPropertyName("size")] string? Size,
    [property: JsonPropertyName("tags")] IReadOnlyDictionary<string, string>? Tags);

public sealed record FfprobeStream(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("codec_type")] string? CodecType,
    [property: JsonPropertyName("codec_name")] string? CodecName,
    [property: JsonPropertyName("width")] int? Width,
    [property: JsonPropertyName("height")] int? Height,
    [property: JsonPropertyName("duration")] string? Duration,
    [property: JsonPropertyName("avg_frame_rate")] string? AverageFrameRate,
    [property: JsonPropertyName("time_base")] string? TimeBase,
    [property: JsonPropertyName("tags")] IReadOnlyDictionary<string, string>? Tags);

public sealed record FfprobeFrame(
    [property: JsonPropertyName("media_type")] string? MediaType,
    [property: JsonPropertyName("stream_index")] int StreamIndex,
    [property: JsonPropertyName("key_frame")] int KeyFrame,
    [property: JsonPropertyName("pkt_pts_time")] string? PacketPtsTime,
    [property: JsonPropertyName("pts_time")] string? PtsTime,
    [property: JsonPropertyName("best_effort_timestamp_time")] string? BestEffortTimestampTime,
    [property: JsonPropertyName("pict_type")] string? PictureType);

public sealed record FfprobePacket(
    [property: JsonPropertyName("codec_type")] string? CodecType,
    [property: JsonPropertyName("stream_index")] int StreamIndex,
    [property: JsonPropertyName("pts_time")] string? PtsTime,
    [property: JsonPropertyName("dts_time")] string? DtsTime,
    [property: JsonPropertyName("flags")] string? Flags);
