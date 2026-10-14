namespace Vmerger.Desktop.ViewModels;

public interface ITrailEditorCallbacks
{
    void OnSelectedClipChanged(ClipTimelineViewModel? previous, ClipTimelineViewModel? current);
    void OnClipAdded(ClipTimelineViewModel clip);
    void ReportEditStatus(string message);
}
