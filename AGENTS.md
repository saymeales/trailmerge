# AGENTS.md — vmerger

## Project goal

Implement a lightweight local desktop application for merging action-camera recordings of MTB trails. A single trail is often recorded across multiple files because recording is stopped and restarted or the camera automatically splits recordings.

The user selects a folder and recording period, receives suggested file groups, adjusts their contents, names them, and trims individual clips at keyframes. The application exports each trail as a single file without re-encoding video or audio.

This is an MVP implementation brief, not a specification for a full video editor.

## Technology and constraints

- C# / .NET, Avalonia UI, MVVM.
- LibVLCSharp with Avalonia integration for playback.
- ffprobe and ffmpeg as separate processes; no FFmpeg C# bindings.
- Do not use Electron, an embedded web frontend, or Python for the application.
- Windows x64 is the initial target platform. The architecture must allow macOS and Linux support.
- Select compatible stable dependency versions using current official documentation and pin them. Do not assume Avalonia and LibVLCSharp version compatibility without checking.
- For the MVP, user-installed ffmpeg/ffprobe and VLC are acceptable, with configurable paths and clear diagnostics. Automatic downloading and bundling of native dependencies are a separate stage.
- All media processing is local; no network services are required.

## Agent working rules

1. First inspect the existing repository and applicable instructions. Adapt the structure to existing code where present.
2. Implement in small, working stages. Start by validating the media pipeline and player.
3. Make routine reversible decisions independently. Ask for clarification only when there is significant ambiguity.
4. Do not expand the MVP with effects, transitions, arbitrary frame-by-frame editing, or re-encoding.
5. Do not replace stream copy with re-encoding when errors or incompatibilities occur.
6. Do not modify, delete, or overwrite original recordings.
7. Do not hide errors or unverified assumptions about timestamps, keyframes, or platform support.
8. Add a README covering startup, dependencies, limitations, and completed checks.
9. Do not claim the GUI or media pipeline has been tested when only a build was completed. Explicitly identify checks that require the user's machine.

## User workflow

1. Select a folder containing videos.
2. Set the recording interval and maximum gap between clips in the same group.
3. Scan recordings, read metadata, sort them, and suggest trail groups.
4. Select a trail to inspect its name, files, order, and total selected duration.
5. Add or remove clips, move a clip to another group, split or merge groups, and change clip order.
6. Select and play a clip; set its start and end boundaries with keyframe snapping.
7. Set the trail name and export folder.
8. Export the selected trail or all ready trails while monitoring progress.
9. Save the project and resume later if needed.

## Data model and architecture

Suggested structure:

- `Vmerger.Core`: models, grouping, range rules, and project validation.
- `Vmerger.Media`: ffprobe, keyframe indexing, compatibility checks, export planning and execution.
- `Vmerger.Desktop`: Avalonia Views/ViewModels, player adapter, dialogs, and settings.
- `Vmerger.Tests`: rule tests and media pipeline integration checks.

Core must not depend on Avalonia, VLC, or FFmpeg processes. Do not introduce additional complex infrastructure without a clear need.

Main entities:

- SourceClip: stable ID, path, file size and modification time, stream metadata, duration, recording start time, and the source and reliability of that time.
- KeyframeIndex: keyframe positions on the clip timeline, with information for mapping to original timestamps/time bases.
- ClipSelection: reference to a SourceClip and a selected range.
- Trail: ID, name, and ordered list of ClipSelection entries.
- Project: format version, source folder, grouping settings, groups, and export directory.
- ExportPlan: explicitly selected streams, ranges, output path, checks, and warnings.

Do not store selected boundaries only in the UI or VLC state. The project model and ffprobe index are authoritative.

## Import and recording times

- MVP: scan the selected folder without recursion for common action-camera containers, primarily MP4/MOV. The extension is a preliminary filter; ffprobe validates the file.
- Read metadata asynchronously with bounded concurrency, cancellation, and progress reporting.
- An error in one file must not stop the entire import: show the file and the reason.
- Do not treat filesystem timestamps as reliable recording times.
- Inspect available tags in real recordings. Explicitly define container/stream metadata precedence; show warnings for conflicts.
- Prefer verified recording start times from metadata. If unavailable, use a documented filesystem-time fallback and mark it as low confidence.
- Do not assume every creation_time represents the start of recording: verify the camera's semantics.
- Interpret timestamps without a time zone explicitly through an import time-zone setting. Preserve both the original value and normalized time.
- Do not guess date formats in filenames without a separate verified rule.
- The MVP period filter applies to recording start time: inclusive start, exclusive end. The UI must make this clear.
- Estimated recording end = start + duration. Do not present this estimate as a separately verified camera timestamp.

## Grouping heuristic

- Sort clips by normalized start time, then deterministically by path.
- Gap = next clip's start time − previous clip's estimated end time.
- If 0 <= gap <= configured threshold, include the next clip in the current group; otherwise start a new group.
- Mark negative gaps as overlaps/uncertainties and start a new group by default.
- The initial threshold is 2 minutes and is configurable. This is a practical heuristic, not guaranteed trail recognition.
- Show the time source and gap between adjacent files.
- Do not group recordings solely by filename similarity.
- Regrouping after manual edits requires an explicit user action and a warning that group contents will be replaced.
- Preserve selected trims when moving clips between groups. When adding a file again, use its full range by default.
- A source recording may be used in multiple ClipSelection entries; the UI must clearly show reuse.

## MVP interface

One main screen:

- Import panel: folder, period, grouping threshold, scan, and cancel.
- Trail list: name, clip count, duration, and validation status.
- Selected trail's clip list: filename, time, range, and order; controls for changing its contents.
- Video area with play/pause, current time, and a timeline showing keyframes.
- Previous/next keyframe, set start/end, and reset range buttons.
- Trail name, output directory, export selected/all, progress, and errors.

Move and reorder buttons are sufficient for the MVP; drag-and-drop is optional.
Implement transport controls and the timeline using Avalonia; use an existing media component for video decoding and rendering.
Place controls beside the video surface; do not require overlays on top of the native VideoView.

## Keyframes, seeking, and boundaries

- Extract the index from the selected video stream using ffprobe, not VLC heuristics.
- Index lazily when opening a clip and before export; cache based on source size/modification time.
- Account for start_time, nonzero/negative timestamps, time bases, and PTS/DTS differences. Do not use rounded seconds as the sole source of truth.
- Snap user-initiated timeline seeking to the nearest keyframe; choose the previous keyframe in a tie.
- Previous/next keyframe actions must select adjacent index entries, not seek by a relative number of seconds.
- During playback, the playhead moves freely; snapping applies to manual navigation and boundary selection.
- Setting start/end from the current position must also snap to a keyframe and display the actual selected time.
- Start is inclusive and end is exclusive: [In, Out). The frame at Out belongs to the excluded portion.
- Allow EOF as a separate boundary even if it is not a keyframe. Use file start and EOF for the full range; validate problematic starts separately.
- Reject empty or reversed ranges and boundaries outside the file.
- An exact selected timestamp does not mean VLC has already displayed the corresponding frame after seeking. Verify paused seeking, seek completion, and the actual preview using real files.
- If VLC is insufficiently accurate, retain it for playback and obtain selected-boundary previews by decoding frames with ffmpeg. Preview decoding is allowed and does not change stream-copy export.
- Do not promise arbitrary frame-accurate trimming.
- Do not treat the key_frame flag alone as proof of independent decoding for every GOP. Check H.264/HEVC and open GOP behavior; reject or explicitly diagnose unsupported cases.

## Export without re-encoding

Mandatory: selected video and audio streams are copied using stream copy (`-c copy`). No hidden transcoding fallback.

Before export:

- Verify that sources exist and have not changed since indexing.
- Validate ranges, output paths, and names.
- Check selected-stream compatibility for concatenation: codec, codec configuration/extradata, dimensions, pixel format, time base, frame rates, audio parameters, channel layout, and other relevant stream structure.
- Preserve the main video stream and a consistent set of audio streams. Camera telemetry, additional data streams, and subtitles are outside the MVP; report their exclusion.
- If clips differ in audio presence or structure, the MVP rejects the group with an explanation.
- The MVP output container is MP4 for compatible streams; reject unsupported combinations.

Implement and verify a two-stage approach: trim individual segments into temporary files using stream copy, then use the concat demuxer and stream copy to produce the result. Select commands and the intermediate container based on actual checks. Do not assume an untested `-ss/-to` template guarantees the intended boundaries.

- Account for B-frames, preroll, timestamp offsets, and possible packet-level audio boundary inaccuracies.
- Do not promise sample-exact audio boundaries with stream copy. Determine and document acceptable deviations for verified formats.
- Do not use the concat protocol as a universal container-merging solution.
- Escape ffconcat paths correctly, including spaces, Unicode, and apostrophes; reject unsupported paths with a clear error.
- Timestamp normalization must not silently conceal missing frames, duplicated segments, or synchronization errors.
- After export, validate the result with ffprobe and decoding; exit code 0 alone is insufficient proof of correct joins.
- Derive the filename from the trail name, sanitizing invalid characters. Resolve collisions with a suffix; do not overwrite existing output.
- Write to a temporary output file first and publish the final filename only after successful validation.
- Cancellation stops processes and removes only the current operation's temporary files.
- Originals always remain unchanged.

## Process execution and reliability

- Launch using ProcessStartInfo.ArgumentList, without a shell or concatenating arguments into a command string.
- Read stdout/stderr concurrently, check exit codes, and parse ffprobe JSON.
- Support CancellationToken, metadata timeouts, and termination of the child process tree.
- Use ffmpeg's machine-readable progress output and update the UI without blocking.
- Bound in-memory log size; retain diagnostic error details.
- Place temporary files in a unique operation directory; clean up in finally.
- Diagnose missing ffmpeg, ffprobe, and native VLC separately.
- All long-running operations are asynchronous; the UI remains responsive.
- Store tool paths and settings in the user's configuration directory.

## Project persistence

- Versioned JSON without embedded media.
- Save trail contents and order, names, ranges, settings, and source references.
- Prefer paths relative to the source folder, with an option to select a new folder after relocation.
- Show missing or changed files in the UI; do not substitute files based solely on matching names.
- Save atomically. Loading must not automatically start an export.
- The keyframe cache may be stored separately; do not consider it valid after a source changes.

## Implementation stages

1. **Media prototype.** Build a minimal Avalonia screen, open a real file through VLC, extract metadata and keyframes using ffprobe, and verify paused seeking. Perform a short manual stream-copy trim and merge; check boundaries and audio.
2. **Core and import.** Models, times/period filtering, grouping heuristic, cancellation, and per-file errors.
3. **Editing.** Group contents/order, names, player, keyframe navigation, and ranges.
4. **Export.** Compatibility checks, plan, temporary segments, concatenation, progress, cancellation, and result validation.
5. **Project persistence and MVP completion.** JSON, dependency settings, README, and Windows testing. Test other platforms separately; do not claim support based solely on a successful dotnet publish.

Leave a working application after each stage and briefly record completed checks and limitations. If the early prototype reveals a stack issue, resolve it in a minimal example first.

## Checks and acceptance criteria

Unit tests:

- Grouping: inclusive threshold, large gaps, overlaps, identical timestamps, and unreliable times.
- Snapping: previous/next, equal distance, start, EOF, and invalid ranges.
- Project serialization and preservation of manual edits.
- Output names, collisions, and stream compatibility planning.

Integration checks:

- Short synthetic H.264/AAC files with known GOPs and distinguishable frames/audio; test HEVC separately if support is claimed.
- Multiple clips from a real camera, including automatic splitting and recording stops.
- Start/end trimming, full ranges, and multiple consecutive joins.
- Nonzero timestamps, B-frames, variable frame rates, and open GOPs: support with verification or explicitly reject.
- Sources without audio, incompatible clips, a damaged file, and Unicode/spaces/apostrophes in paths.
- Verify start/end and frames around joins by decoding, along with duration and A/V synchronization.
- Confirm no re-encoding using actual arguments and logs; matching codec names alone is insufficient.
- Scan/export cancellation, cleanup, and no overwriting of originals or completed outputs.

The MVP is ready when the user can import a folder, receive groups, adjust them manually, preview clips and select keyframe boundaries, save the project, and export a compatible trail without re-encoding. Errors are clear, the UI stays responsive, sources remain unchanged, and limitations are documented in the README.
