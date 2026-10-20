# Beta 8 debugging and design notes

This iteration keeps WinMovie's source-range editing flow while making playback state, timeline timing, and project changes explicit.

## Findings and fixes

| Failure | Root cause | Fix and edge cases |
| --- | --- | --- |
| Opening or skimming footage could start playback or stop after a few frames | MediaPlayer autoplay and source-opening state were treated as actual user playback; loading another frame could leave a pending seek behind | Disable autoplay, track explicit playback intent, keep the latest seek during opening, and reset playback intent when media ends. Source skimming cannot interrupt requested playback. |
| Titles, music, and video did not line up | Video used fixed-width cards while overlay tracks used seconds; timing calculations were copied across the editor and exporter | Share `TimelineLayout` for transition overlaps, clip starts, duration, source lookup, and export joins. All tracks now share a single scroller and time scale, with zoom, a ruler, and a playhead. Only visible ruler labels are created. |
| Range dragging conflicted with selecting footage | Native dragging and filmstrip range selection competed for pointer events | Use captured pointer gestures with a movement threshold, preserve the selected range, and calculate drop positions relative to the actual timeline. The same gesture path moves title and music cards while preserving their grab offset. Clip dragging scrolls near timeline edges. |
| Short source audio could cut off video | `-shortest` ended an encoded segment when the audio stream ran out | Pad audio with silence and trim it to the expected segment length. Bound the final output duration, including projects with looped title images. |
| Normalization could erase a volume adjustment | Loudness normalization ran after volume gain | Normalize first, then apply the requested gain. Resample and pad the resulting audio consistently. |
| Adding delayed music reduced existing audio before music began | FFmpeg's audio mixer normalized every input, including delayed silence | Mix with normalization disabled and apply one limiter after the final mix. |
| Simple title punctuation could fail to render | FFmpeg interpreted percent signs as text expansion syntax | Treat title text literally with expansion disabled. |
| Hardware selection did not speed the preparation of exported clips | Segment rendering used software encoding during final exports regardless of the chosen encoder | Apply detected encoding to both clip preparation and the final join, with software fallback. Cache identity excludes transition-only settings because those are applied during joining. |
| Title edits could disappear when clicking another clip | Drafts were cleared as part of ordinary selection changes | Apply changed drafts on navigation, provide fixed Apply/Cancel controls, and record committed changes in undo history. Discard during New/Open/Close really discards the draft. |
| Shortcuts could stop working after rebuilding cards | Selection refresh removed focused controls, leaving focus in an unrelated field | Register file and history shortcuts at the window level and restore focus after history and selection changes. Text fields retain native undo and deletion. |
| Concurrent commands or dialogs could fail or affect another project | Multiple asynchronous UI actions had no shared ownership or dialog gate | Serialize project commands and dialogs, disable unavailable actions, snapshot exports, and cancel background rendering on window close. Preview progress has its own display and cannot overwrite save messages. |
| Invalid projects or changed media failed late | Deserialized collections, ranges, colors, and enum values were trusted; stored media metadata could be stale | Validate before replacing the open project; re-probe linked files for export; report shortened source media; allow missing video relinking; reject exports over original source paths. |

## Organization

- `TimelineLayout.cs` owns shared timing calculations.
- `ProjectDocument.cs` validates, clones, serializes, and migrates legacy titles.
- `EditHistory.cs` maintains bounded undo/redo with branch replacement after a new edit.
- `MainWindow.Workflows.cs` contains command ownership, dialog serialization, history restoration, shared pointer gestures, and timeline navigation.
- Thumbnail work is limited to two files at a time and cancelled when changing projects. Preview cache copies run asynchronously, completed cache entries are committed atomically, and the size limit is enforced after writes.
- The primary toolbar groups file actions and uses command overflow. Clip and transition controls appear in focused flyouts. Title color and advanced style controls use collapsible sections.
- Interactive media, clips, tracks, transitions, sliders, and progress displays have accessible names. Clip context menus provide reordering without dragging. Keyboard focus is kept out of hidden editors after selection changes.

## Verification scope

The automated smoke suite renders actual MP4s and checks video/audio presence, source-limited sizes, transitions, title graphics with transparency and shadow/outline, title timing, fading music, hardware selection, and software output. Regression checks cover malformed projects, shared timeline timing, legacy-title migration, undo/redo branches, source overwrite protection, early-ending audio, normalized volume gain, delayed-music levels, literal title punctuation, and cancellation.

Manual Windows UI checks cover importing, precise source selection, range insertion, clip reordering, undo/redo, Space playback/pause, title editing and cancellation, shared timeline seeking, compact window layout, saving, exporting, and launch from the extracted distributable. NVIDIA and software encoding are exercised on this machine. Intel and AMD device-specific execution still requires testing on those devices.

This remains a beta build. The checks establish the covered behavior; they do not substitute for longer editing sessions with diverse camera formats and graphics drivers.
