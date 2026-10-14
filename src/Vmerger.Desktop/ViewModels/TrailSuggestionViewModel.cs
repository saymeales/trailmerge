using System.Collections.ObjectModel;
using Vmerger.Core.Grouping;

namespace Vmerger.Desktop.ViewModels;

public sealed class TrailSuggestionViewModel : ObservableObject
{
    private string _name;
    private string _summary;

    public TrailSuggestionViewModel(TrailSuggestion trail)
        : this(trail.Name, trail.Clips.Select(static clip => new ClipTimelineViewModel(clip)))
    {
    }

    public TrailSuggestionViewModel(string name, IEnumerable<ClipTimelineViewModel> clips)
    {
        _name = name;
        Clips = new ObservableCollection<ClipTimelineViewModel>(clips);
        _summary = "";
        RefreshSummary();
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public ObservableCollection<ClipTimelineViewModel> Clips { get; }

    public void RefreshSummary()
    {
        var totalDuration = TimeSpan.FromTicks(Clips.Sum(static clip => clip.Duration.Ticks));
        Summary = Clips.Count == 0
            ? "0 clips"
            : $"{Clips.Count} clip(s), {totalDuration:hh\\:mm\\:ss}, first {Clips[0].StartText}";
    }
}
