# CLAUDE.md — VideoCropper

Context for working on this project with Claude Code. Read this before making changes.

## What this is

A Windows desktop app (C# / WPF / .NET 8) that crops, resizes, and re-encodes video by
driving a **local FFmpeg install** as a child process. It is a GUI front-end over `ffmpeg`
and `ffprobe`; it does not decode or encode anything itself. The output is a single native
`.exe`.

## Core design principle

Everything the user configures funnels into **one FFmpeg invocation**. The app's job is:
1. Read the source video's dimensions + duration (`ffprobe`).
2. Let the user define a crop region (visually) and/or a target size.
3. Assemble an FFmpeg argument list.
4. Run it and report progress.

Keep that funnel intact. New features should mean new arguments in the filter/encode chain,
not a new processing path.

## Stack

- **Language/UI:** C# 12, WPF, .NET 8 (LTS)
- **External binaries:** `ffmpeg.exe` and `ffprobe.exe` (NOT bundled — see Configuration)
- **No third-party NuGet packages required.** Process control is `System.Diagnostics.Process`;
  JSON parsing is `System.Text.Json`. Keep dependencies minimal unless there's a strong reason.
- **IDE:** Visual Studio 2022 Community (or `dotnet` CLI).

## File map

| File | Responsibility |
|------|----------------|
| `CropCommandBuilder.cs` | Pure logic. Turns a `CropSettings` object into an FFmpeg argument **list**. Owns the aspect-lock / even-number rules. No I/O, no process spawning — keep it that way so it stays unit-testable. |
| `FfmpegService.cs` | All process I/O: `ProbeAsync` (dimensions+duration), `ExtractFrameAsync` (preview still), `RunWithProgressAsync` (the encode + progress reporting). |
| `MainWindow.xaml` | UI layout: file picker, crop canvas, resize controls, audio controls, format picker, progress bar. *(To be built.)* |
| `MainWindow.xaml.cs` | Wires the UI to the two services above. Builds a `CropSettings` from the controls and calls `RunWithProgressAsync`. *(To be built.)* |

## Current status

- ✅ `CropCommandBuilder.cs` — complete and correct.
- ✅ `FfmpegService.cs` — complete and correct.
- ⛔ `MainWindow.xaml` / `MainWindow.xaml.cs` — not yet written. This is the main remaining work.
- ⛔ A settings store for the FFmpeg path — not yet written (path is currently passed to
  `FfmpegService` at construction).

## Key behaviors / invariants (do not break these)

- **Even dimensions only.** H.264/H.265 with `yuv420p` requires even width AND height.
  `CropCommandBuilder.EnsureEven` throws on any odd literal dimension. The UI should also
  prevent odd entry, but the builder is the hard backstop — leave it in place.
- **Aspect lock uses `-2`.** When `CropSettings.AspectLock` is true, set only ONE scale axis;
  the builder emits `scale=W:-2` or `scale=-2:H` so FFmpeg preserves ratio and forces even.
  `-2`, never `-1` (which can produce odd numbers). Lock off = both axes literal = stretch.
- **Filter order is crop-then-scale:** `-vf "crop=...,scale=..."`. Never reverse it.
- **Argument LIST, never a joined string.** `ProcessStartInfo.ArgumentList` is used so paths
  with spaces work without quoting. Don't refactor to a single `Arguments` string.
- **Progress parses `out_time=`**, not `out_time_ms`/`out_time_us` (whose units have shifted
  across FFmpeg builds). `out_time` is `HH:MM:SS.micro` and unambiguous.
- **Drain stderr.** `RunWithProgressAsync` calls `BeginErrorReadLine()` so the stderr pipe
  can't fill and deadlock FFmpeg. Keep it.

## Encoding matrix

| Output | Video codec | Audio codec | Notes |
|--------|-------------|-------------|-------|
| mp4 | `h264_nvenc` (default) or `libx264` | `aac` | NVENC = GPU, much faster. `-pix_fmt yuv420p` for compatibility. |
| webm | `libvpx-vp9` | `libopus` | VP9 is CPU-only; consumer NVENC can't encode it, so `UseNvenc` is ignored for webm. |

Audio modes: `Keep` (re-encode), `Mute` (`-an`), `Volume` (`-af volume=X` + re-encode).

Future option: AV1 via `av1_nvenc` is available on RTX 40-/50-series if a third format is wanted.

## Configuration

FFmpeg is not bundled. Two acceptable approaches:
1. **Hardcoded path** (consistent with prior tooling): `C:\FFMPEG\bin\ffmpeg.exe` and
   `ffprobe.exe`. Simplest for single-machine use.
2. **Settings field** (preferred if distributing to others): a "Browse for FFmpeg" path stored
   in app settings, validated on startup.

## Build / publish

Single self-contained exe (runs on any Windows machine, no .NET install needed):

```
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Use `--self-contained false` for a tiny exe when the .NET 8 runtime is already present.

## Conventions

- Prefer PowerShell over batch for any helper scripts.
- Keep `CropCommandBuilder` free of side effects so it can be covered by simple unit tests
  (good first test targets: even-number rejection, crop-out-of-bounds, `-2` emission).
- When adding a new encode option, extend `CropSettings` + `CropCommandBuilder` together and
  surface the control in `MainWindow.xaml`; don't special-case it in the UI code-behind.

## Likely next tasks (in rough order)

1. Build `MainWindow.xaml`: file picker → preview `Canvas` with draggable/resizable crop
   `Rectangle` (a `Thumb` for move, corner `Thumb`s for resize).
2. Map the on-screen crop rect back to source pixels:
   `srcCoord = round(screenCoord * SourceWidth / DisplayedImageWidth)`, then snap even + clamp.
3. Resize controls: slider + editable number box per axis, with live aspect linking when locked.
4. Wire audio + format controls into `CropSettings`.
5. Run button → `RunWithProgressAsync`, bound to a progress bar; catch builder exceptions and
   show the message (this is how the odd-number / out-of-bounds errors reach the user).
6. Optional polish: scrub slider to re-grab the preview frame, drag-and-drop input,
   remembered output folder, a "copy exact FFmpeg command" button.
