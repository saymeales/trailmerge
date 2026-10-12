namespace Vmerger.Media.Import;

public sealed class MediaFileScanner
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
        ".mov",
        ".m4v"
    };

    public IReadOnlyList<string> FindCandidates(string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        if (!Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException($"Folder does not exist: {folderPath}");
        }

        return Directory
            .EnumerateFiles(folderPath, "*", SearchOption.TopDirectoryOnly)
            .Where(static path => SupportedExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
