# Changelog

## v1.0-beta.8

- Simplify the editor with a grouped file toolbar, focused clip/transition settings, fixed title Apply/Cancel actions, clear loading and save states, and accessible names and shortcuts.
- Align title, video, and music tracks on one shared timeline with a ruler, zoom, playhead, and common scrolling. Use direct gestures for source range insertion, clip reordering, and track positioning.
- Add bounded undo/redo, preserve title drafts during navigation, migrate legacy clip titles, validate projects before opening, and offer missing-video relinking.
- Disable unintended media autoplay, preserve pending seeks during loading, isolate preview progress, and cancel background work when closing.
- Preserve video when source audio ends early; apply volume after normalization; prevent delayed music from lowering the original audio; render title punctuation literally.
- Use detected hardware encoding during clip preparation as well as final export, report final encoding progress, protect source files from export overwrite, and check linked media for changes.
- Add regression checks and debugging notes, and refresh the README and screenshot.

## v1.0-beta.7

- Make timeline clip reordering work with a direct drag gesture and highlighted insertion point.
- Give export progress a solid foreground panel over a dimmed, blurred editor, and offer Open file and Open folder when export completes.
- Prompt to save or discard changes before closing, starting a new movie, or opening another project. Mark Discard changes in red and autosave previously saved projects every 30 seconds after edits.
- Add an application screenshot to the README.

## v1.0-beta.6

- Render live titles with the same installed font file and graphics path as export. Add a configurable transparent, blurred shadow and a per-letter pixel outline.
- Make source filmstrips wider for more precise range selection, and support Delete for selected title and music tracks when a parameter field is not being edited.
- Label transition timing as duration in seconds, and route clip card drops to timeline reordering.
- Enable native spelling correction in the title text field.
- Cache unchanged preview clip segments and automatically try a detected hardware H.264 encoder, falling back to software when needed.
- Place new titles at the current timeline position for five seconds by default, and suggest the saved project name for MP4 export.

## v1.0-beta.5

- Move titles above the main timeline and music below it; shorten all three tracks to give the media bin and preview more height.
- Scale preview video within its available area while preserving its aspect ratio, with title placement aligned to the displayed video frame.
- Show all Windows installed fonts in the title editor with each font name rendered in that face. Add native text and background color pickers with RGB and HEX entry.
- Preview title text, placement, style, and color over the video while editing; add separate fade in and fade out controls for timed titles and render their fades into exports.
- Handle Space before focused buttons and ignore key repeat so playback toggles once per press.

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
