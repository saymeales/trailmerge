using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Vmerger.Media.Export;

public sealed class StreamCopyExportService(string ffmpegPath = "ffmpeg")
{
    public async Task ExportAsync(
        IReadOnlyList<ExportClipRange> clips,
        string outputPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        IProgress<ExportProgressUpdate>? detailedProgress = null)
    {
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        if (clips.Count == 0)
        {
            throw new ExportException("Nothing to export: selected trail has no clips.");
        }

        if (File.Exists(outputPath))
        {
            throw new ExportException($"Output already exists: {outputPath}");
        }

        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ExportException("Output path must include a directory.");
        }

        Directory.CreateDirectory(outputDirectory);
        var workDirectory = Path.Combine(outputDirectory, $".vmerger-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);

        try
        {
            var segments = new List<string>();
            for (var index = 0; index < clips.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var segmentPath = Path.Combine(workDirectory, $"segment-{index:D4}.mp4");
                progress?.Report($"Creating segment {index + 1}/{clips.Count}: {Path.GetFileName(clips[index].Path)}");
                var stage = $"Creating segment {index + 1}/{clips.Count}: {Path.GetFileName(clips[index].Path)}";
                detailedProgress?.Report(new ExportProgressUpdate(0.8 * index / clips.Count, stage));
                var segmentIndex = index;
                await CreateSegmentAsync(clips[index], segmentPath, progress, cancellationToken,
                    fraction => detailedProgress?.Report(new ExportProgressUpdate(
                        0.8 * (segmentIndex + fraction) / clips.Count, stage))).ConfigureAwait(false);
                segments.Add(segmentPath);
            }

            var concatFile = Path.Combine(workDirectory, "segments.ffconcat");
            await File.WriteAllTextAsync(concatFile, BuildConcatFile(segments), new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);

            var tempOutput = Path.Combine(workDirectory, "output.mp4");
            progress?.Report("Concatenating segments");
            detailedProgress?.Report(new ExportProgressUpdate(0.8, "Concatenating segments"));
            await RunAsync(
                [
                    "-hide_banner",
                    "-y",
                    "-f", "concat",
                    "-safe", "0",
                    "-i", concatFile,
                    "-map", "0:v:0",
                    "-map", "0:a?",
                    "-c", "copy",
                    "-movflags", "+faststart",
                    tempOutput
                ],
                progress,
                cancellationToken,
                clips.Sum(SelectedDuration),
                fraction => detailedProgress?.Report(new ExportProgressUpdate(0.8 + 0.19 * fraction, "Concatenating segments")))
                .ConfigureAwait(false);

            File.Move(tempOutput, outputPath);
            progress?.Report($"Wrote {outputPath}");
            detailedProgress?.Report(new ExportProgressUpdate(1, "Completed"));
        }
        finally
        {
            try
            {
                if (Directory.Exists(workDirectory))
                {
                    Directory.Delete(workDirectory, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private async Task CreateSegmentAsync(
        ExportClipRange clip,
        string segmentPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        Action<double>? reportFraction)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-y"
        };

        if (clip.StartSeconds is { } start)
        {
            args.Add("-ss");
            args.Add(FormatSeconds(start));
        }

        if (clip.EndSeconds is { } end)
        {
            args.Add("-to");
            args.Add(FormatSeconds(end));
        }

        args.AddRange(
        [
            "-i", clip.Path,
            "-map", "0:v:0",
            "-map", "0:a?",
            "-c", "copy",
            "-avoid_negative_ts", "make_zero",
            segmentPath
        ]);

        await RunAsync(args, progress, cancellationToken, SelectedDuration(clip), reportFraction).ConfigureAwait(false);
    }

    private async Task RunAsync(
        IReadOnlyList<string> arguments,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        double durationSeconds,
        Action<double>? reportFraction)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.ArgumentList.Insert(0, "-nostats");
        startInfo.ArgumentList.Insert(0, "pipe:1");
        startInfo.ArgumentList.Insert(0, "-progress");
        progress?.Report($"ffmpeg {FormatCommandArguments(startInfo.ArgumentList)}");

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                throw new ExportException("ffmpeg did not start.");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new ExportException($"Could not start ffmpeg at '{ffmpegPath}'. Configure ffmpeg or add it to PATH.", ex);
        }

        var stdoutTask = ReadProcessOutputAsync(process.StandardOutput, null, cancellationToken, line =>
        {
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && durationSeconds > 0 &&
                long.TryParse(line[12..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds))
                reportFraction?.Invoke(Math.Clamp(microseconds / 1_000_000d / durationSeconds, 0, 1));
        });
        var stderrTask = ReadProcessOutputAsync(process.StandardError, progress, cancellationToken);

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
            var details = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new ExportException($"ffmpeg failed with exit code {process.ExitCode}: {details.Trim()}");
        }
    }

    private static string BuildConcatFile(IEnumerable<string> segments)
    {
        var builder = new StringBuilder();
        builder.AppendLine("ffconcat version 1.0");

        foreach (var segment in segments)
        {
            builder.Append("file '");
            builder.Append(segment.Replace("\\", "/").Replace("'", "'\\''"));
            builder.AppendLine("'");
        }

        return builder.ToString();
    }

    private static string FormatSeconds(double seconds)
    {
        return seconds.ToString("0.######", CultureInfo.InvariantCulture);
    }

    private static double SelectedDuration(ExportClipRange clip) =>
        Math.Max(0, (clip.EndSeconds ?? clip.DurationSeconds) - (clip.StartSeconds ?? 0));

    private static async Task<string> ReadProcessOutputAsync(
        StreamReader reader,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        Action<string>? onLine = null)
    {
        var builder = new StringBuilder();

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            builder.AppendLine(line);
            if (builder.Length > 80_000) builder.Remove(0, builder.Length - 80_000);
            progress?.Report(line);
            onLine?.Invoke(line);
        }

        return builder.ToString();
    }

    private static string FormatCommandArguments(IEnumerable<string> arguments)
    {
        return string.Join(" ", arguments.Select(QuoteArgument));
    }

    private static string QuoteArgument(string argument)
    {
        return argument.Any(char.IsWhiteSpace) || argument.Contains('"')
            ? $"\"{argument.Replace("\"", "\\\"")}\""
            : argument;
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
        catch
        {
        }
    }
}

public sealed record ExportClipRange(string Path, double? StartSeconds, double? EndSeconds, double DurationSeconds = 0);
public sealed record ExportProgressUpdate(double Fraction, string Stage);

public sealed class ExportException : Exception
{
    public ExportException(string message)
        : base(message)
    {
    }

    public ExportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
