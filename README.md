# WinMovie

A small, native WinUI 3 video editor built around the quick editing flow of iMovie '08–'11.

Current preview: **v1.0-beta.3**. The next feature iteration will be beta 4; release candidates and the stable release will be named when the maintainer decides they are ready.

## What works

- Import MP4, MOV, MKV, AVI, M4V, and WebM video through a file picker.
- Each imported clip has a six-frame filmstrip in the upper-left media bin. Hover to skim, drag across it to select a range, or drag a yellow edge to refine the range. Drag the yellow selection itself onto the timeline, or use the Add button. The same source can be used repeatedly.
- Timeline clips show filmstrip previews. Drag clips to insertion points to reorder them, or use the arrow buttons. Select a clip to trim its in/out points, play it, or remove it.
- Click a timeline boundary and choose Cut, Dissolve, Fade through Black/White, Wipe Left/Right, or Slide Left/Right from the illustrated menu. Set its duration in seconds. Press Delete to clear a selected transition or remove a selected clip.
- Choose center, lower-third, or opening title presets, then click the title in the preview to edit its text.
- Set clip volume from 0 to 4×, normalize audio, and enter a speed percentage from 25% to 400% in a dialog that compares original and new duration and offers a keep-pitch choice.
- Save and reopen `.winmovie` projects. Timeline playback uses a low-resolution background render that includes transitions, titles, speed, fades, and audio processing.
- Export H.264/AAC MP4 at a chosen size from 480p through 4K, limited by the smallest source video in the timeline. The app probes NVIDIA, Intel, and AMD encoders and chooses an available hardware encoder automatically, with software fallback. A modal progress overlay supports cancellation.

Source files remain in place, so moving them will break a saved project's media links.

## Build and run

Requires Windows 10 2004 or later (Windows 11 recommended), the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), and [FFmpeg](https://ffmpeg.org/download.html) with `ffmpeg` and `ffprobe` on `PATH`. The project uses Windows App SDK 1.8 and builds as an unpackaged, self-contained WinUI 3 app.

```powershell
dotnet restore WinMovie.csproj
dotnet run --project WinMovie.csproj -p:Platform=x64
```

For a distributable folder:

```powershell
dotnet publish WinMovie.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64
```

The output is under `bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/publish/`. FFmpeg is a separate runtime dependency; put its executables on `PATH` on the target PC.

Keep the complete publish folder together when distributing the app. In particular, `WinMovie.pri` must sit beside `WinMovie.exe`; the project checks for it during publish.

## Editing flow

1. Import one or more videos.
2. Skim an imported video's filmstrip and drag to highlight a range. Drag inside the yellow range to a timeline card or `+` boundary, or click **Add range to timeline**.
3. Drag timeline clips to reorder them. Click a boundary for the transition menu and duration. Select a clip to trim it or set its title, speed, and audio. Click the title directly in the preview to type. Use Delete on the selected clip or boundary.
4. Let the timeline preview finish rendering in the background, then play it. Save the project and use **Export MP4** to pick a source-limited resolution.

### Current limits

There are no separate audio tracks, effects browser, or image clips yet. Timeline preview is rendered at 480p and may take time after edits. Export uses 30 fps and stereo 48 kHz audio.
