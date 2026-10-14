# vmerger

Local desktop app for grouping, trimming, and merging action-camera recordings using FFmpeg stream copy.

## Dependencies

- .NET 8 SDK or newer; current desktop target: Windows x64.
- Avalonia and LibVLCSharp; pinned versions are in `Directory.Packages.props` and restored by NuGet.
- Native VLC libraries are provided by `VideoLAN.LibVLC.Windows`.
- `ffmpeg` and `ffprobe` on `PATH`, or in a `tools` directory beside the executable.

## Build and Run

```sh
dotnet build vmerger.sln
dotnet run --project src/Vmerger.Desktop/Vmerger.Desktop.csproj
```

## Checks

```sh
dotnet run --project src/Vmerger.Tests/Vmerger.Tests.csproj
```

Checks cover rules, project persistence, editing, and simulated export processes. Real-media playback, joins, and A/V synchronization require manual verification. macOS and Linux deployment are not verified.

Source code only. `scripts/Build-Windows.ps1` can create a local self-contained build using an installed FFmpeg/ffprobe pair.
