# vmerger

Local desktop MVP for merging action-camera trail recordings without re-encoding.

## Current stage

This repository currently contains only the stage 1 media prototype:

- `Vmerger.Core` for domain models.
- `Vmerger.Media` for ffprobe process execution.
- `Vmerger.Desktop` for a minimal Avalonia window that opens a real media file with LibVLCSharp and displays ffprobe metadata/keyframes.
- Folder/file import scans MP4/MOV/M4V candidates, reads ffprobe metadata, extracts DJI-oriented recording times, and suggests trail groups with a two-minute gap heuristic.

## Requirements

- .NET 8 runtime or newer SDK.
- Windows x64 for the current desktop prototype.
- `ffprobe` available on `PATH`.
- VLC/libvlc native binaries are supplied for Windows by the pinned `VideoLAN.LibVLC.Windows` package.

## Run

```powershell
dotnet run --project src/Vmerger.Desktop/Vmerger.Desktop.csproj
```

Use **Open media file** to select an MP4/MOV/MKV/AVI file. The window requests VLC playback and runs ffprobe twice: once for metadata and once for video keyframes.

Use **Import folder** to scan all candidate recordings in a folder, or **Import files** to process only selected recordings.

## Current limitations

- No editing workflow, trimming, export, project persistence, or settings UI yet.
- Folder import is a first-pass prototype: metadata parsing currently has a DJI-oriented reader plus a low-confidence filesystem fallback.
- Keyframe snapping is wired to the seek slider, but paused seek preview accuracy is not yet verified against decoded frames.
- ffmpeg stream-copy trim and merge checks are not implemented in this slice.
- The GUI/media pipeline still requires manual testing on the target machine with real camera files.
