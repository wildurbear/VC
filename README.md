# Video Cropper

A lightweight Windows desktop app for cropping, resizing, and re-encoding video. It gives you
a visual crop box on the actual video frame, aspect-ratio–safe resizing, audio control, and
mp4/webm output — all by driving your own local **FFmpeg** install. Hardware-accelerated
encoding (NVENC) is used by default on NVIDIA GPUs.

---

## Features

- **Visual crop** — drag and resize a box directly on a frame of your video.
- **Resize** with a slider or by typing exact numbers.
- **Aspect-lock toggle** — keeps proportions so nothing stretches; turn it off to stretch on
  purpose.
- **Odd-number protection** — refuses odd dimensions (video can't use them) with a clear error.
- **Audio control** — keep, mute, or lower the volume.
- **Output format** — mp4 (H.264) or webm (VP9).
- **GPU acceleration** — uses NVENC for fast mp4 encodes on NVIDIA cards.
- **Live progress bar** while encoding.

---

## Requirements

1. **Windows 10 or 11** (64-bit).
2. **FFmpeg** installed locally — you need both `ffmpeg.exe` and `ffprobe.exe`.
   - Download a Windows build from <https://www.gyan.dev/ffmpeg/builds/> (the "full" or
     "essentials" release build).
   - Extract it. By default this app looks in `C:\FFMPEG\bin\`, so placing `ffmpeg.exe` and
     `ffprobe.exe` there is the zero-config option. (You can also point the app at any other
     location — see Configuration.)
3. **.NET 8 Desktop Runtime** — only required if you use the *framework-dependent* build.
   The *self-contained* build includes it and needs nothing extra.

> A NVIDIA GPU is recommended for fast mp4 encoding but is **not** required — the app falls
> back to CPU encoding.

---

## Installing FFmpeg (one-time)

1. Download a build from the link above and unzip it.
2. Copy `ffmpeg.exe` and `ffprobe.exe` from its `bin` folder into `C:\FFMPEG\bin\`.
3. (Optional) Verify from PowerShell:
   ```powershell
   C:\FFMPEG\bin\ffmpeg.exe -version
   ```

---

## Running the app

1. Download/build `VideoCropper.exe` (see Deployment).
2. Double-click it. No installer needed.
3. **Open a video** with the file picker.
4. The first frame appears with a crop box overlay. Drag and resize the box to set the crop.
5. (Optional) Set a target size. With **aspect lock on**, type one dimension and the other
   follows automatically. Turn the lock off to set both and stretch.
6. Choose **audio**: keep, mute, or lower volume.
7. Choose **output format**: mp4 or webm.
8. Click **Run**. Watch the progress bar; your output file is written when it finishes.

If you enter an odd width or height (e.g. `1081`), the app stops and tells you — video
dimensions must be even numbers.

---

## Configuration

The app needs to know where FFmpeg lives. Default is `C:\FFMPEG\bin\`. If you keep FFmpeg
elsewhere, set the FFmpeg folder in the app's settings (or update the path passed to
`FfmpegService` if you're building from source).

---

## Deployment (building the .exe yourself)

### Prerequisites
- [Visual Studio 2022 Community](https://visualstudio.microsoft.com/) with the **.NET desktop
  development** workload, **or** the [.NET 8 SDK](https://dotnet.microsoft.com/download)
  for command-line builds.

### Build a single, self-contained .exe
Runs on any Windows 64-bit machine with no .NET install required:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The `.exe` lands in `bin\Release\net8.0-windows\win-x64\publish\`.

### Build a small, framework-dependent .exe
Smaller file, but each machine needs the **.NET 8 Desktop Runtime** installed:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

> **Note:** The `.exe` does **not** bundle FFmpeg. Anyone you share it with still needs FFmpeg
> installed (see Requirements). Distribute the FFmpeg install instructions alongside the app.

---

## Project structure

```
VideoCropper/
├─ CropCommandBuilder.cs   # Builds the FFmpeg argument list (crop/scale/audio/format rules)
├─ FfmpegService.cs        # Probes video, extracts preview frames, runs encode with progress
├─ MainWindow.xaml         # UI layout
├─ MainWindow.xaml.cs      # Wires the UI to the services
└─ CLAUDE.md               # Architecture notes for extending the app
```

---

## Troubleshooting

| Problem | Likely cause / fix |
|---------|--------------------|
| "FFmpeg not found" on launch | `ffmpeg.exe`/`ffprobe.exe` aren't at `C:\FFMPEG\bin\`. Move them there or set the path in settings. |
| Odd-number error | Width or height is odd. Use even numbers (the aspect-lock toggle keeps them even automatically). |
| mp4 encode is slow | NVENC may be unavailable (non-NVIDIA GPU or old drivers); the app is using CPU. Update GPU drivers, or accept CPU speed. |
| webm encode is slow | Expected — VP9 (webm) is CPU-only; NVENC can't encode it. Use mp4 for speed. |
| App won't start, mentions .NET | You have the framework-dependent build but no .NET 8 Desktop Runtime. Install the runtime, or use the self-contained build. |
| Crop "falls outside the source video" error | The crop box + offset exceed the original dimensions. Shrink the box or move it inward. |

---

## License

Add your preferred license here (e.g. MIT) before sharing publicly.
