using System.Collections.Specialized;
using System.ComponentModel;
using System.Collections.ObjectModel;
using Vmerger.Desktop.ViewModels;

namespace Vmerger.Desktop.Services;

/// <summary>Observes every editable trail and range, including clips outside the current selection.</summary>
public interface IProjectState : INotifyPropertyChanged
{
    ObservableCollection<TrailSuggestionViewModel> Trails { get; }
    bool ContinueToNextClipOnEnd { get; }
    string? ExportDirectory { get; }
    string ProjectStatus { get; set; }
}

public sealed class ProjectAutosave(IProjectState owner, TemporaryProjectStore store) : IDisposable
{
    private readonly HashSet<TrailSuggestionViewModel> _trails = [];
    private readonly HashSet<ClipTimelineViewModel> _clips = [];
    private Task _pending = Task.CompletedTask;
    private bool _enabled;

    public void Start()
    {
        _enabled = true;
        owner.Trails.CollectionChanged += ContentsChanged;
        owner.PropertyChanged += OwnerChanged;
        Rewire();
    }

    private void OwnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(owner.ContinueToNextClipOnEnd) or nameof(owner.ExportDirectory)) Save();
    }

    private void ContentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Rewire();
        Save();
    }

    private void Rewire()
    {
        foreach (var trail in _trails)
        {
            trail.PropertyChanged -= Edited;
            trail.Clips.CollectionChanged -= ContentsChanged;
        }
        foreach (var clip in _clips) clip.PropertyChanged -= Edited;
        _trails.Clear();
        _clips.Clear();
        foreach (var trail in owner.Trails)
        {
            if (_trails.Add(trail))
            {
                trail.PropertyChanged += Edited;
                trail.Clips.CollectionChanged += ContentsChanged;
            }
            foreach (var clip in trail.Clips)
                if (_clips.Add(clip)) clip.PropertyChanged += Edited;
        }
    }

    private void Edited(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TrailSuggestionViewModel.Name) or
            nameof(ClipTimelineViewModel.StartSeconds) or nameof(ClipTimelineViewModel.EndSeconds)) Save();
    }

    private void Save()
    {
        if (!_enabled) return;
        var snapshot = new SavedProject(1, owner.Trails.Select(trail => new SavedTrail(trail.Name,
            trail.Clips.Select(clip => new SavedClip(clip.SourceClip, clip.StartSeconds, clip.EndSeconds)).ToArray())).ToArray(),
            owner.ContinueToNextClipOnEnd, owner.ExportDirectory);
        _pending = SaveAfterAsync(_pending, snapshot);
    }

    private async Task SaveAfterAsync(Task previous, SavedProject snapshot)
    {
        await previous;
        try
        {
            await store.SaveAsync(snapshot);
            owner.ProjectStatus = "Project: saved automatically";
        }
        catch (Exception ex)
        {
            owner.ProjectStatus = $"Project: autosave failed — {ex.Message}";
        }
    }

    public Task FlushAsync() => _pending;

    public void Dispose()
    {
        _enabled = false;
        owner.Trails.CollectionChanged -= ContentsChanged;
        owner.PropertyChanged -= OwnerChanged;
        foreach (var trail in _trails)
        {
            trail.PropertyChanged -= Edited;
            trail.Clips.CollectionChanged -= ContentsChanged;
        }
        foreach (var clip in _clips) clip.PropertyChanged -= Edited;
        _trails.Clear();
        _clips.Clear();
    }
}
