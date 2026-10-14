# vmerger

Local desktop MVP for merging action-camera trail recordings without re-encoding.

## Current stage

This repository contains a desktop prototype with import, trail editing, stream-copy export, and automatic project recovery:

- `Vmerger.Core` for domain models.
- `Vmerger.Media` for ffprobe process execution.
- `Vmerger.Desktop` for a minimal Avalonia window that opens a real media file with LibVLCSharp and displays ffprobe metadata/keyframes.
- Folder/file import scans MP4/MOV/M4V candidates, reads ffprobe metadata, extracts DJI-oriented recording times, and suggests trail groups with a two-minute gap heuristic.
- Trail names, contents, order, and keyframe-based ranges can be edited.
- Temporary projects are saved automatically and restored at startup.

## Requirements

- .NET 8 runtime or newer SDK.
- Windows x64 for the current desktop prototype.
- `ffprobe` and `ffmpeg` available on `PATH`.
- VLC/libvlc native binaries are supplied for Windows by the pinned `VideoLAN.LibVLC.Windows` package.

## Run

```powershell
dotnet run --project src/Vmerger.Desktop/Vmerger.Desktop.csproj
```

Use **Import folder** to scan all candidate recordings in a folder, or **Import files** to process only selected recordings.

The app saves edits automatically to `%LOCALAPPDATA%\vmerger\temporary-project.json` on Windows (the platform local application data directory elsewhere). This is a recovery project rather than an OS temporary file. Versioned JSON stores source paths/metadata, trail contents/order, names, ranges, continue-playback preference, and the last export directory. Media is never copied into the project. Paths currently remain absolute; relocation requires re-import. Playback position and keyframe caches are not persisted.

On startup, an existing project is restored without starting playback or export. Missing or changed recordings are diagnosed and blocked from playback/export until re-imported. Corrupt or unsupported JSON remains untouched, with autosave disabled and an error shown.

After **Export all** completes every trail successfully, **Close project?** offers to clear the project. **Yes** drains pending saves, deletes only recovery JSON, and clears the editor. Recordings and completed exports remain. **No**, closing the dialog, or any export failure keeps the project. Normal application exit flushes pending saves. Editing and application exit are blocked while importing/exporting.

The highlighted export panel shows overall estimated percentage, current trail, and segment/concatenation stage using ffmpeg's machine-readable progress. The percentage measures staged work, not estimated time remaining. Detailed logs remain in **Tools > Open diagnostics**.

## Current limitations

- There is no named-project Save As/relocation workflow or dependency settings UI yet.
- Folder import is a first-pass prototype: metadata parsing currently has a DJI-oriented reader plus a low-confidence filesystem fallback.
- Keyframe snapping is wired to the seek slider, but paused seek preview accuracy is not yet verified against decoded frames.
- The existing export pipeline uses `-c copy` for segments and concatenation, but comprehensive compatibility checks and post-export decoded boundary/A/V validation remain incomplete. Completion currently means the export processes succeeded and the output was published; it is not proof of accurate joins.
- The GUI/media pipeline still requires manual testing on the target machine with real camera files.

## Player component refactor

Native playback, transport controls, media switching/preloading, UI event dispatch, and VLC disposal are owned by PlayerController. MainWindowViewModel retains trail selection and trim rules; existing view bindings are preserved. Validation: solution build only; play/pause, seeking, clip transitions, and window-close cleanup require a manual GUI check with real recordings.

## Color scheme

All application color values live in `src/Vmerger.Desktop/Themes/Colors.axaml`. `Themes/AppStyles.axaml` maps those roles to shared styles, and `App.axaml` supplies the same colors to the Fluent controls. The app uses an explicit light theme with neutral desktop surfaces, dark text, white inputs, and square controls inspired by classic Windows applications. Video overlays keep their own dark surface and light text for readability.

Neutral colors distinguish canvas, panels, raised clip rows, borders, and secondary metadata. Blue indicates primary actions, selection, active export, and selected trim ranges; both trim handles use the same treatment. Red indicates destructive actions and export failures, while green indicates a completed export. Amber is reserved for warning states. Trail names are labeled white edit fields. Trail-management controls sit above the list; export controls sit below it, followed by a flat status area that is visually distinct from draggable clip cards. Action buttons, menus, and dialog titles use title case. The main window, diagnostics, and project-close dialog share these styles.

Validation: XAML/solution build and a scan confirming that application color literals are confined to the palette. Visual appearance, hover/focus/disabled states, and native video rendering still need a manual GUI review.

## Playback transitions

`PositionSeconds` is presentation only. Explicit out boundaries are checked against VLC's current time on native time notifications. Full-file ranges complete on a native EOF notification or the automatic EOF pause produced by `play-and-pause`; manual pauses are tracked separately. This avoids requiring the last time sample to equal the metadata duration. Queued callbacks from before a media change or seek are discarded, and terminal handling is idempotent.

Start/join previews open the clip with a `start-time` option rather than seeking immediately after opening an input. Preview ranges and preloaded starts use each source clip's own duration, independently of the UI's current duration. Playback time remains a VLC timestamp, not proof that a particular frame has been displayed, and native time notifications do not guarantee frame-accurate trimmed transitions.

Completed checks: solution build and range-rule checks for native EOF below metadata duration, explicit out boundaries, unset end boundaries, and short clip previews. Manual checks still required: full-file join previews with no Out selected, trimmed joins, manual pause/resume near EOF, seeking away from EOF, rapid clip switching, and continuation with real camera files. Automatic EOF pause detection assumes playback is controlled through `PlayerController`.

## Completed project/progress checks

```powershell
dotnet build vmerger.sln
dotnet run --project src/Vmerger.Tests/Vmerger.Tests.csproj
```

The dependency-free check executable verifies automatic saves for names, order, moved/reused clips and trims, settings/metadata round trips, source preservation, atomic-save staging cleanup, pending-save draining before deletion, and invalid JSON/version/range rejection. It also exercises progress parsing, stages, stream-copy arguments, output collisions, and failure cleanup using a simulated ffmpeg child process. It does not decode or validate real media.

Manual checks still required on Windows: edit and restart to restore a project, review missing/changed sources, watch real exports, and verify that the close-project prompt appears only after a completely successful export-all and that Yes/No behave correctly. Real camera boundary, synchronization, and GUI playback checks remain outstanding. Other platforms have not been tested.
