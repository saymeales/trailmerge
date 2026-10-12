using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Vmerger.Media.Ffprobe;

public sealed class FfprobeService(string ffprobePath = "ffprobe")
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<FfprobeResult> ReadMetadataAsync(string mediaPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mediaPath);

        var arguments = new[]
        {
            "-v", "error",
            "-print_format", "json",
            "-show_format",
            "-show_streams",
            mediaPath
        };

        var json = await RunAsync(arguments, cancellationToken).ConfigureAwait(false);
        return Deserialize(json);
    }

    public async Task<IReadOnlyList<FfprobeFrame>> ReadKeyframesAsync(string mediaPath, CancellationToken cancellationToken = default)
    {
         ArgumentNullException.ThrowIfNull(mediaPath);

         var arguments = new[]
         {
             "-v", "error",
             "-select_streams", "v:0",
             "-skip_frame", "nokey",
             "-show_frames",
             "-show_entries", "frame=media_type,stream_index,key_frame,pkt_pts_time,pts_time,best_effort_timestamp_time,pict_type",
             "-print_format", "json",
             mediaPath
         };

         var json = await RunAsync(arguments, cancellationToken).ConfigureAwait(false);
         return Deserialize(json).Frames ?? [];
    }

    public async Task<IReadOnlyList<FfprobePacket>> ReadKeyframePacketsAsync(string mediaPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mediaPath);

        var arguments = new[]
        {
            "-v", "error",
            "-select_streams", "v:0",
            "-show_packets",
            "-show_entries", "packet=codec_type,stream_index,pts_time,dts_time,flags",
            "-print_format", "json",
            mediaPath
        };

        var json = await RunAsync(arguments, cancellationToken).ConfigureAwait(false);
        return (Deserialize(json).Packets ?? [])
            .Where(static packet => packet.Flags?.Contains('K', StringComparison.Ordinal) == true)
            .ToArray();
    }

    private async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("ffprobe did not start.");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new FfprobeException($"Could not start ffprobe at '{ffprobePath}'. Configure ffprobe or add it to PATH.", ex);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new FfprobeException(FormatError(process.ExitCode, stderr));
        }

        return stdout;
    }

    private static FfprobeResult Deserialize(string json)
    {
        var result = JsonSerializer.Deserialize<FfprobeResult>(json, JsonOptions);
        return result ?? throw new FfprobeException("ffprobe returned empty or invalid JSON.");
    }

    private static string FormatError(int exitCode, string stderr)
    {
        var trimmed = stderr.Trim();
        return trimmed.Length == 0
            ? $"ffprobe failed with exit code {exitCode}."
            : $"ffprobe failed with exit code {exitCode}: {trimmed}";
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}

public sealed class FfprobeException : Exception
{
    public FfprobeException(string message)
        : base(message)
    {
    }

    public FfprobeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
