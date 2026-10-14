namespace Vmerger.Media;

public static class MediaToolPaths
{
    /// <summary>Prefer packaged tools; preserve explicit paths and development PATH fallback.</summary>
    public static string Resolve(string configuredPath)
    {
        if (configuredPath is not ("ffmpeg" or "ffprobe")) return configuredPath;
        var fileName = OperatingSystem.IsWindows() ? configuredPath + ".exe" : configuredPath;
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", fileName);
        return File.Exists(bundled) ? bundled : configuredPath;
    }
}
