# Photos, color, and audio in beta 9

## Photos and color clips

Import JPEG, PNG, BMP, WebP, or TIFF files with **Import media**. Add a photo to the movie like video footage. Photos start at five seconds; change their duration in **Clip settings → Picture and color**. Animated image formats are not supported as animations.

Choose **＋ Color** in the movie toolbar to create a five-second black clip. **Color and duration** includes a Windows color picker with RGB and HEX input. Put a title above it and use the normal transition controls to fade into the next clip.

Framing choices are **Fit** (whole image with bars where necessary), **Fill** (crop the edges to fill the movie frame), and **Stretch** (change proportions to fill the frame). Fit preserves source resolution; Fill and Stretch explicitly allow scaling to fill the chosen frame. Export sizes remain bounded by the smallest linked visual source. A movie containing only generated color can export up to 4K.

## Picture and titles

**Clip settings → Picture and color** offers Original, Warm, Cool, Cinema, and Black and white looks. Expand Adjustments for brightness, contrast, color intensity, and warmth. The frame updates while you adjust; Apply commits one undoable edit, and Cancel restores the previous picture. Multiple selected clips can share appearance settings.

The title Style menu adds **Caption** (a box behind the text), **Corner** (upper left), and **Cinema** (letterbox bars with a lower title). All retain font, color, background opacity, shadow, outline, and fade controls. The same graphics renderer supplies live title preview and final export.

## General audio and enhanced dialogue

The former Music track is now **Audio**. It mixes music, dialogue, and effects, including overlapping files. Use **Audio ▾** to add a file or select any existing sound, including a sound covered by another card. Audio starts at the playhead and defaults to full volume without fades. Set exact timeline position, source trim, normalization, volume, and independent fades in its settings. Audio beyond the end of the movie is trimmed on export.

For enhanced dialogue:

1. Select one video clip, then choose **Clip settings → Audio tools → Extract audio as WAV**.
2. Save the WAV and enhance it in your preferred audio editor or service. The WAV is stereo 48 kHz, 24-bit PCM. It contains the edited source range at its current playback speed, before clip volume or loudness processing.
3. On the same clip, choose **Replace audio** and select the enhanced result. WinMovie aligns it with the clip and mutes the original audio. Undo restores the previous state.

Replacement audio follows its clip when reordered, trimmed, or sped up. The saved link remembers the extraction range and speed; portions outside the enhanced file become silence. A sync offset in Audio settings provides fine timing correction: positive values delay the sound. Uncheck **Follow video clip** to position it independently. Deleting the video also removes its linked replacement; Undo restores both. Removing only replacement audio leaves the original clip muted; raise its clip volume to restore original sound.

The app never uploads audio to an enhancement service. It works with the local file you choose. It does not detect speech offsets automatically; preserve the extracted WAV's length and leading silence when processing it.

## Compatibility and implementation

Existing project files still open. Video, title, and transition enum values retain their previous numbers, and the serialized `Music` collection remains compatible. New appearance settings default to the previous rendering behavior. Photos and generated colors use the same segment, transition, title, cache, and export pipeline as video. Linked audio placement is shared by timeline drawing and mixing; stable clip identifiers survive project saves and undo/redo.

The smoke suite checks actual pixels for Fit/Fill/Stretch, photo import, generated-color resolutions, all color looks and new title styles, mixed-media transitions, PCM WAV metadata and duration, and replacement audio after reorder/trim/speed changes, sync offsets, overlaps, fades, and uncovered ranges. It also retains the beta 8 regression checks. Filter behavior follows the [FFmpeg filter documentation](https://ffmpeg.org/ffmpeg-filters.html).
