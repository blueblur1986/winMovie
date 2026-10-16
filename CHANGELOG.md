# Changelog

## v1.0-beta.4

- Ctrl-click timeline clips to select multiple, or Ctrl+A to select all, then apply common speed, volume, normalization, fades, and incoming transitions or delete them together.
- Press Space over a source filmstrip to play from the hovered frame and Space again to pause; Space plays or pauses the selected timeline. Keep the Play/Pause button synchronized with actual playback state.
- Add a timed title track with independent start and duration, drag-to-position cards, font, color, background, opacity, shadow, outline, and placement controls. Titles can cross clip boundaries.
- Add a timed background music track with source trimming, volume, drag-to-position, and optional end fade.
- Widen the timeline drop target and route mouse wheel input over filmstrips to the media bin's vertical scroller.
- Use dark native window chrome and embed the app icon correctly in the EXE while setting window and taskbar icons.

## v1.0-beta.3

- Render a low-resolution timeline preview in the background so playback includes transitions, titles, speed, fades, and audio processing.
- Drag the yellow source selection directly into the timeline; accept drops on clip cards as well as insertion boundaries.
- Press Delete to remove a selected timeline clip or clear a selected transition.
- Replace the transition shelf with an illustrated dropdown and the speed presets with a percentage dialog that compares clip durations.
- Detect a working NVIDIA, Intel, or AMD encoder automatically; offer source-limited export sizes from 480p through 4K.
- Show a modal export progress overlay with cancellation and add an original WinUI-style app icon.

## v1.0-beta.2

- Put six-frame filmstrips inside the upper-left media bin, with skimming, yellow range selection, and adjustable range edges.
- Add drag-and-drop insertion of selected source ranges and reordering of timeline clips.
- Add draggable transition presets for dissolve, fades through black or white, wipes, and slides, with adjustable durations.
- Add direct in/out trimming for timeline clips.
- Include the WinUI resource index (`WinMovie.pri`) in the Windows ZIP and verify the extracted application launches.

## v1.0-beta.1

- Initial WinUI 3 editor with importing, source range selection, timeline editing, title presets, speed and audio controls, project save/open, and MP4 export.
