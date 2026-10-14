namespace Vmerger.Desktop.ViewModels;

/// <summary>Player notifications are delivered on the UI thread.</summary>
public interface IPlayerCallbacks
{
    void UpdatePlaybackPosition();
    void OnPlayerPlaying();
    void OnPlayerPaused();
    void OnPlayerStopped();
    void OnPlayerTimeChanged();
    void OnPlayerEndReached();
    void BeforePlaybackResume();
}
