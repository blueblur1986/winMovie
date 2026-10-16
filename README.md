# WinMovie

A small, native WinUI 3 video editor built around the quick editing flow of iMovie '08–'11.

## What works

- Import MP4, MOV, MKV, AVI, M4V, and WebM video through a file picker.
- Each imported clip has a six-frame filmstrip in the upper-left media bin. Hover to skim, drag across it to select a range, or drag a yellow edge to refine the range. Drag the selected range onto a timeline insertion point or use the Add button. The same source can be used repeatedly.
- Timeline clips show filmstrip previews. Drag clips to insertion points to reorder them, or use the arrow buttons. Select a clip to trim its in/out points, play it, or remove it.
- Click a timeline boundary and choose Cut, Dissolve, Fade through Black/White, Wipe Left/Right, or Slide Left/Right. Transition tiles can also be dragged onto boundaries. Set their duration in seconds. Apply a fade at the first or last boundary.
- Choose center, lower-third, or opening title presets, then click the title in the preview to edit its text.
- Set clip volume from 0 to 4×, normalize audio, and change speed from 0.25× to 4× with a keep-pitch choice.
- Save and reopen `.winmovie` projects, and export H.264/AAC MP4. The export dialog offers software encoding plus available NVIDIA, Intel, or AMD encoders, with software fallback if the hardware encoder cannot run.

The editor preview shows clip cuts, speed, basic volume, and title placement. The rendered MP4 applies the selected transitions, fades, normalization, and full volume settings. Source files remain in place, so moving them will break a saved project's media links.

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
2. Skim an imported video's filmstrip and drag to highlight a range. Drag **Drag selected range to timeline** onto a `+` boundary, or click **Add range to timeline**.
3. Drag timeline clips to reorder them. Click a boundary to choose a transition and duration. Select a clip to trim it or set its title, speed, and audio. Click the title directly in the preview to type.
4. Save the project and use **Export MP4**.

### Current limits

The preview plays transitions as cuts; the MP4 export contains the transitions. Titles and audio normalization are rendered on export. There are no separate audio tracks, effects browser, image clips, or background rendering yet. Export standardizes the picture to 1080p at 30 fps and audio to stereo 48 kHz.
