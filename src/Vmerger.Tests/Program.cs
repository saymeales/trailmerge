using System.Collections.ObjectModel;
using Vmerger.Core.Media;
using Vmerger.Desktop.Services;
using Vmerger.Desktop.ViewModels;
using Vmerger.Media.Export;

// Child mode is a deterministic process stand-in, not a real media encoder.
if (args.Contains("-progress"))
{
    if (!args.Contains("pipe:1") || !args.Contains("copy")) return 3;
    if (args.Any(argument => argument.EndsWith("fail.mp4", StringComparison.Ordinal)))
    {
        Console.Error.WriteLine("Simulated export failure");
        return 2;
    }
    Console.WriteLine("out_time_us=0");
    Console.WriteLine("out_time_us=2000000");
    Console.WriteLine("out_time_us=N/A");
    Console.WriteLine("progress=end");
    await File.WriteAllTextAsync(args[^1], "simulated output");
    return 0;
}

var directory = Path.Combine(Path.GetTempPath(), "vmerger-project-check-" + Guid.NewGuid().ToString("N"));
var fullRange = new PlaybackRange(10, null, null);
Check(!fullRange.HasReachedEnd(9.96, false), "full range awaits native EOF");
Check(fullRange.HasReachedEnd(9.96, true), "EOF below metadata duration still completes");
Check(!fullRange.HasReachedEnd(10, false), "rounded clock alone is not EOF");
Check(fullRange.PreviewStartSeconds(3) == 7, "unset end preview uses source duration");
var trimmedRange = new PlaybackRange(10, 2, 6);
Check(!trimmedRange.HasReachedEnd(5.9, false) && trimmedRange.HasReachedEnd(6, false), "explicit out boundary");
Check(trimmedRange.PreviewStartSeconds(3) == 3, "trimmed join preview");
var shortRange = new PlaybackRange(2, 1, null);
Check(shortRange.PreviewStartSeconds(3) == 1 && shortRange.EndSeconds == 2, "short clip uses its own range");
Console.WriteLine("PASS: native EOF versus explicit out, source-local preview duration and short ranges.");
Directory.CreateDirectory(directory);
try
{
    var path = Path.Combine(directory, "project.json");
    var store = new TemporaryProjectStore(path);
    Check(await store.LoadAsync() is null, "missing project");
    var sourcePath = Path.Combine(directory, "camera's trail ü.mp4");
    await File.WriteAllTextAsync(sourcePath, "original recording bytes");
    var sourceInfo = new FileInfo(sourcePath);
    var source = new SourceClip(sourcePath, sourceInfo.Length, sourceInfo.LastWriteTimeUtc,
        TimeSpan.FromSeconds(10), new RecordingTimeInfo(DateTimeOffset.UtcNow, "test", RecordingTimeConfidence.Low, "original"));
    var clip = new ClipTimelineViewModel(source);
    var state = new TestProject();
    using var autosave = new ProjectAutosave(state, store);
    autosave.Start();
    var first = new TrailSuggestionViewModel("Original name", [clip]);
    state.Trails.Add(first);
    first.Name = "Manual name";
    clip.StartSeconds = 2;
    clip.EndSeconds = 8;
    var reused = new ClipTimelineViewModel(source) { StartSeconds = 3, EndSeconds = 7 };
    first.Clips.Add(reused);
    first.Clips.Move(1, 0);
    var second = new TrailSuggestionViewModel("Second", []);
    state.Trails.Add(second);
    first.Clips.Remove(clip);
    second.Clips.Add(clip);
    clip.StartSeconds = 4;
    state.ContinueToNextClipOnEnd = true;
    state.ExportDirectory = directory;
    await autosave.FlushAsync();
    var loaded = (await store.LoadAsync())!;
    Check(loaded.Trails.Length == 2 && loaded.Trails[0].Name == "Manual name", "trail edits");
    Check(loaded.Trails[0].Clips[0].StartSeconds == 3 && loaded.Trails[1].Clips[0].StartSeconds == 4,
        "reorder, move and trims on unselected clips");
    Check(loaded.Trails[1].Clips[0].EndSeconds == 8, "preserved end boundary");
    Check(loaded.ContinueToNextClip && loaded.ExportDirectory == directory, "settings");
    Check(loaded.Trails[0].Clips[0].Source == source, "source metadata and Unicode path round trip");
    Check(await File.ReadAllTextAsync(sourcePath) == "original recording bytes", "source unchanged");
    Check(Directory.GetFiles(directory).Length == 2, "metadata only and staging cleanup");
    // A queued save must finish before cleaning a project, or it could recreate the JSON.
    second.Name = "Pending edit";
    autosave.Dispose();
    await autosave.FlushAsync();
    store.Delete();
    second.Name = "Detached edit";
    await autosave.FlushAsync();
    Check(!File.Exists(path), "close drains writes and detaches observers");
    await File.WriteAllTextAsync(path, "{\"FormatVersion\":99,\"Trails\":[]}");
    await ExpectInvalid(store, "unsupported version");
    Check((await File.ReadAllTextAsync(path)).Contains("99"), "failed restore preserves JSON");
    await File.WriteAllTextAsync(path, "{broken");
    try { await store.LoadAsync(); throw new Exception("Malformed JSON accepted"); }
    catch (System.Text.Json.JsonException) { }
    var invalid = loaded with { Trails = [new SavedTrail("Invalid", [new SavedClip(source, 8, 2)])] };
    await store.SaveAsync(invalid);
    await ExpectInvalid(store, "invalid range");
    var updates = new CollectedProgress<ExportProgressUpdate>();
    var logs = new CollectedProgress<string>();
    var export = new StreamCopyExportService(Environment.ProcessPath!);
    var output = Path.Combine(directory, "export.mp4");
    await export.ExportAsync([new ExportClipRange(sourcePath, null, null, 4)], output, logs, detailedProgress: updates);
    Check(updates.Items.Any(update => Math.Abs(update.Fraction - 0.4) < 0.0001), "microsecond progress parsing");
    Check(updates.Items.Any(update => update.Stage == "Concatenating segments") && updates.Items[^1].Fraction == 1,
        "segment, concat and completed stages");
    Check(updates.Items.All(update => update.Fraction is >= 0 and <= 1), "bounded progress");
    Check(logs.Items.Where(line => line.StartsWith("ffmpeg ")).All(line => line.Contains("-c copy") && line.Contains("-progress pipe:1")),
        "stream-copy and progress process arguments");
    Check(Directory.GetDirectories(directory).Length == 0, "export staging cleanup");
    try
    {
        await export.ExportAsync([new ExportClipRange(sourcePath, null, null, 4)], output);
        throw new Exception("Existing export overwritten");
    }
    catch (ExportException) { }
    try
    {
        await export.ExportAsync([new ExportClipRange(Path.Combine(directory, "fail.mp4"), null, null, 4)],
            Path.Combine(directory, "failed-output.mp4"));
        throw new Exception("Failed process accepted");
    }
    catch (ExportException) { }
    Check(!File.Exists(Path.Combine(directory, "failed-output.mp4")) && Directory.GetDirectories(directory).Length == 0,
        "failed export preserves outputs and cleans staging");
    Console.WriteLine("PASS: autosave, edits, settings, JSON restore, source preservation, cleanup, invalid project checks.");
    Console.WriteLine("PASS: progress/process orchestration using a simulated ffmpeg process (no real media validation).");
}
finally { Directory.Delete(directory, recursive: true); }
return 0;

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception("Failed: " + name);
}

static async Task ExpectInvalid(TemporaryProjectStore store, string name)
{
    try { await store.LoadAsync(); throw new Exception("Accepted " + name); }
    catch (InvalidDataException) { }
}

sealed class TestProject : ObservableObject, IProjectState
{
    private bool _continue;
    private string? _directory;
    public ObservableCollection<TrailSuggestionViewModel> Trails { get; } = [];
    public bool ContinueToNextClipOnEnd { get => _continue; set => SetProperty(ref _continue, value); }
    public string? ExportDirectory { get => _directory; set => SetProperty(ref _directory, value); }
    public string ProjectStatus { get; set; } = "";
}

sealed class CollectedProgress<T> : IProgress<T>
{
    private readonly List<T> _items = [];
    public T[] Items { get { lock (_items) return _items.ToArray(); } }
    public void Report(T value) { lock (_items) _items.Add(value); }
}
