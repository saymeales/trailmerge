using System.Text.Json;
using Vmerger.Core.Media;

namespace Vmerger.Desktop.Services;

public sealed record SavedClip(SourceClip Source, double? StartSeconds, double? EndSeconds);
public sealed record SavedTrail(string Name, SavedClip[] Clips);
public sealed record SavedProject(int FormatVersion, SavedTrail[] Trails, bool ContinueToNextClip, string? ExportDirectory);

/// <summary>Stores metadata only. Each save replaces the previous JSON atomically.</summary>
public sealed class TemporaryProjectStore(string path)
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "vmerger", "temporary-project.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public async Task<SavedProject?> LoadAsync()
    {
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        var project = await JsonSerializer.DeserializeAsync<SavedProject>(stream, Options)
            ?? throw new InvalidDataException("Project JSON is empty.");
        if (project.FormatVersion != 1)
            throw new InvalidDataException($"Unsupported project version {project.FormatVersion}.");
        if (project.Trails is null || project.Trails.Any(trail => trail is null || trail.Name is null ||
            trail.Clips is null || trail.Clips.Any(clip => clip is null || clip.Source is null ||
                string.IsNullOrWhiteSpace(clip.Source.Path) || clip.Source.RecordingTime is null)))
            throw new InvalidDataException("Project contains invalid trail or source metadata.");
        foreach (var clip in project.Trails.SelectMany(trail => trail.Clips))
        {
            var start = clip.StartSeconds ?? 0;
            var end = clip.EndSeconds ?? clip.Source.Duration.TotalSeconds;
            if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start ||
                end > clip.Source.Duration.TotalSeconds)
                throw new InvalidDataException($"Invalid saved range for {clip.Source.Path}.");
        }
        return project;
    }

    public async Task SaveAsync(SavedProject project)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, project, Options);
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }
            File.Move(staging, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }

    public void Delete() => File.Delete(path);
}
