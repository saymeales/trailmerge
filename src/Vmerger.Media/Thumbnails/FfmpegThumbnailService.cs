using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Vmerger.Media.Thumbnails;

public sealed class FfmpegThumbnailService(string ffmpegPath = "ffmpeg")
{
    private readonly string _cacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "vmerger",
        "thumbnails");

    public async Task<string> GetThumbnailAsync(
        string sourcePath,
        long sizeBytes,
        DateTimeOffset lastModified,
        double seconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        Directory.CreateDirectory(_cacheDirectory);
        var thumbnailPath = Path.Combine(_cacheDirectory, $"{CreateCacheKey(sourcePath, sizeBytes, lastModified, seconds)}.jpg");
        if (File.Exists(thumbnailPath))
        {
            return thumbnailPath;
        }

        var tempPath = Path.Combine(_cacheDirectory, $"{Path.GetFileNameWithoutExtension(thumbnailPath)}.tmp.jpg");
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }

        await RunFfmpegAsync(sourcePath, tempPath, seconds, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, thumbnailPath, overwrite: true);
        return thumbnailPath;
    }

    private async Task RunFfmpegAsync(
        string sourcePath,
        string thumbnailPath,
        double seconds,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = MediaToolPaths.Resolve(ffmpegPath),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in new[]
        {
            "-hide_banner",
            "-y",
            "-ss", seconds.ToString("0.######", CultureInfo.InvariantCulture),
            "-i", sourcePath,
            "-frames:v", "1",
            "-q:v", "2",
            thumbnailPath
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("ffmpeg did not start.");
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
            var details = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException($"ffmpeg thumbnail failed with exit code {process.ExitCode}: {details.Trim()}");
        }
    }

    private static string CreateCacheKey(string sourcePath, long sizeBytes, DateTimeOffset lastModified, double seconds)
    {
        var value = $"{Path.GetFullPath(sourcePath)}|{sizeBytes}|{lastModified.UtcTicks}|{seconds:0.######}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
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
