using System.Collections.ObjectModel;

namespace Vmerger.Desktop.ViewModels;

public sealed class TrailEditorViewModel(ITrailEditorCallbacks callbacks) : ObservableObject
{
    private int _selectedTrailCount;
    private TrailSuggestionViewModel? _selectedTrail;
    private ClipTimelineViewModel? _selectedClip;
    private ClipTimelineViewModel? _clipClipboard;
    private bool _clipboardCut;
    public ObservableCollection<TrailSuggestionViewModel> Trails { get; } = [];

    public int SelectedTrailCount
    {
        get => _selectedTrailCount;
        set
        {
            if (SetProperty(ref _selectedTrailCount, value))
            {
                OnPropertyChanged(nameof(CanMergeSelectedTrails));
            }
        }
    }

    public bool CanMergeSelectedTrails => SelectedTrailCount >= 2;

    public TrailSuggestionViewModel? SelectedTrail
    {
        get => _selectedTrail;
        set
        {
            if (SetProperty(ref _selectedTrail, value))
            {
                SelectedClip = value?.Clips.FirstOrDefault();
            }
        }
    }

    public ClipTimelineViewModel? SelectedClip
    {
        get => _selectedClip;
        set
        {
            if (ReferenceEquals(_selectedClip, value)) return;
            var previous = _selectedClip;
            _selectedClip = value;
            OnPropertyChanged();
            callbacks.OnSelectedClipChanged(previous, value);
        }
    }

    public void SelectWithoutOpening(TrailSuggestionViewModel? trail, ClipTimelineViewModel? clip)
    {
        _selectedTrail = trail;
        _selectedClip = clip;
        OnPropertyChanged(nameof(SelectedTrail));
        OnPropertyChanged(nameof(SelectedClip));
    }

    public void ClearClipboard()
    {
        _clipClipboard = null;
        _clipboardCut = false;
    }
    public void AddTrail()
    {
        var trail = new TrailSuggestionViewModel($"Trail {Trails.Count + 1}", []);
        Trails.Add(trail);
        SelectedTrail = trail;
    }

    public void DeleteSelectedTrail()
    {
        if (SelectedTrail is null)
        {
            return;
        }

        var index = Trails.IndexOf(SelectedTrail);
        Trails.Remove(SelectedTrail);
        SelectedTrail = Trails.Count == 0 ? null : Trails[Math.Clamp(index, 0, Trails.Count - 1)];
    }

    public void MergeSelectedTrailIntoPrevious()
    {
        if (SelectedTrail is null)
        {
            return;
        }

        var index = Trails.IndexOf(SelectedTrail);
        if (index <= 0)
        {
            return;
        }

        var previous = Trails[index - 1];
        foreach (var clip in SelectedTrail.Clips)
        {
            previous.Clips.Add(clip);
        }

        Trails.RemoveAt(index);
        previous.RefreshSummary();
        SelectedTrail = previous;
    }

    public void MergeSelectedTrails(IReadOnlyList<TrailSuggestionViewModel> selectedTrails)
    {
        ArgumentNullException.ThrowIfNull(selectedTrails);

        var orderedTrails = selectedTrails
            .Distinct()
            .Select(trail => new { Trail = trail, Index = Trails.IndexOf(trail) })
            .Where(static item => item.Index >= 0)
            .OrderBy(static item => item.Index)
            .Select(static item => item.Trail)
            .ToArray();

        if (orderedTrails.Length < 2)
        {
            callbacks.ReportEditStatus("Merge: select at least two trails");
            return;
        }

        var target = orderedTrails[0];
        foreach (var source in orderedTrails.Skip(1))
        {
            foreach (var clip in source.Clips)
            {
                target.Clips.Add(clip);
            }
        }

        foreach (var source in orderedTrails.Skip(1).Reverse())
        {
            Trails.Remove(source);
        }

        target.RefreshSummary();
        SelectedTrail = target;
        SelectedClip = target.Clips.FirstOrDefault();
        callbacks.ReportEditStatus($"Merge: merged {orderedTrails.Length} trails into {target.Name}");
    }

    public void SplitSelectedTrailBeforeSelectedClip()
    {
        if (SelectedTrail is null || SelectedClip is null)
        {
            return;
        }

        var clipIndex = SelectedTrail.Clips.IndexOf(SelectedClip);
        if (clipIndex <= 0)
        {
            return;
        }

        var movedClips = SelectedTrail.Clips.Skip(clipIndex).ToArray();
        foreach (var clip in movedClips)
        {
            SelectedTrail.Clips.Remove(clip);
        }

        SelectedTrail.RefreshSummary();

        var newTrail = new TrailSuggestionViewModel($"{SelectedTrail.Name} split", movedClips);
        var trailIndex = Trails.IndexOf(SelectedTrail);
        Trails.Insert(trailIndex + 1, newTrail);
        SelectedTrail = newTrail;
        SelectedClip = movedClips[0];
    }

    public void RemoveSelectedClip()
    {
        if (SelectedClip is null || FindTrailForClip(SelectedClip) is not { } owner)
        {
            return;
        }

        var index = owner.Clips.IndexOf(SelectedClip);
        owner.Clips.Remove(SelectedClip);
        owner.RefreshSummary();
        SelectedTrail = owner;
        SelectedClip = owner.Clips.Count == 0 ? null : owner.Clips[Math.Clamp(index, 0, owner.Clips.Count - 1)];
    }

    public void DeleteSelection()
    {
        if (SelectedClip is not null)
        {
            RemoveSelectedClip();
            return;
        }

        DeleteSelectedTrail();
    }

    public void DuplicateSelectedClip()
    {
        if (SelectedTrail is null || SelectedClip is null)
        {
            return;
        }

        var index = SelectedTrail.Clips.IndexOf(SelectedClip);
        var duplicate = CreateDuplicateClip(SelectedClip);

        SelectedTrail.Clips.Insert(index + 1, duplicate);
        SelectedTrail.RefreshSummary();
        SelectedClip = duplicate;
    }

    public void SelectClip(TrailSuggestionViewModel trail, ClipTimelineViewModel clip)
    {
        SelectedTrail = trail;
        SelectedClip = clip;
    }

    public void SelectClip(ClipTimelineViewModel clip)
    {
        if (FindTrailForClip(clip) is { } trail)
        {
            SelectClip(trail, clip);
        }
    }

    public void MoveClipToTrail(ClipTimelineViewModel clip, TrailSuggestionViewModel targetTrail)
    {
        MoveClipToTrail(clip, targetTrail, targetTrail.Clips.Count);
    }

    private void MoveClipToTrail(ClipTimelineViewModel clip, TrailSuggestionViewModel targetTrail, int targetIndex)
    {
        if (FindTrailForClip(clip) is not { } sourceTrail || ReferenceEquals(sourceTrail, targetTrail))
        {
            SelectedTrail = targetTrail;
            SelectedClip = clip;
            return;
        }

        sourceTrail.Clips.Remove(clip);
        sourceTrail.RefreshSummary();
        targetTrail.Clips.Insert(Math.Clamp(targetIndex, 0, targetTrail.Clips.Count), clip);
        targetTrail.RefreshSummary();
        SelectedTrail = targetTrail;
        SelectedClip = clip;
    }

    private int GetPasteIndex(TrailSuggestionViewModel targetTrail)
    {
        if (_clipClipboard is not null)
        {
            var sourceIndex = targetTrail.Clips.IndexOf(_clipClipboard);
            if (sourceIndex >= 0)
            {
                return sourceIndex + 1;
            }
        }

        if (SelectedClip is not null && ReferenceEquals(FindTrailForClip(SelectedClip), targetTrail))
        {
            return targetTrail.Clips.IndexOf(SelectedClip) + 1;
        }

        return targetTrail.Clips.Count;
    }

    public void CopySelectedClip()
    {
        if (SelectedClip is null)
        {
            return;
        }

        _clipClipboard = SelectedClip;
        _clipboardCut = false;
    }

    public void CutSelectedClip()
    {
        if (SelectedClip is null)
        {
            return;
        }

        _clipClipboard = SelectedClip;
        _clipboardCut = true;
    }

    public void PasteClipToSelectedTrail()
    {
        if (_clipClipboard is null || SelectedTrail is null)
        {
            return;
        }

        if (_clipboardCut)
        {
            MoveClipToTrail(_clipClipboard, SelectedTrail, GetPasteIndex(SelectedTrail));
            _clipClipboard = null;
            _clipboardCut = false;
            return;
        }

        var duplicate = CreateDuplicateClip(_clipClipboard);
        SelectedTrail.Clips.Insert(GetPasteIndex(SelectedTrail), duplicate);
        SelectedTrail.RefreshSummary();
        SelectedClip = duplicate;
    }

    private ClipTimelineViewModel CreateDuplicateClip(ClipTimelineViewModel source)
    {
        var duplicate = new ClipTimelineViewModel(source.SourceClip);
        callbacks.OnClipAdded(duplicate);
        return duplicate;
    }
    private TrailSuggestionViewModel? FindTrailForClip(ClipTimelineViewModel clip)
    {
        return Trails.FirstOrDefault(trail => trail.Clips.Contains(clip));
    }
}
