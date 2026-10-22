using System.Text.Json;
using WinMovie;

internal static class Beta9Checks
{
    public static async Task RunAsync(string root, MediaAsset video)
    {
        var imagePath = Path.Combine(root, "portrait.png");
        var result = await MediaEngine.RunAsync("ffmpeg", ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=c=yellow:s=120x240", "-frames:v", "1", "-update", "1", imagePath]);
        if (result.ExitCode != 0) throw new Exception(result.Error);
        var image = await MediaEngine.ProbeAsync(imagePath);
        if (image.Kind != MediaKind.Image || image.Duration != 5 || image.HasAudio) throw new Exception("Still images must default to five seconds with no audio.");
        foreach (var framing in Enum.GetValues<ClipFraming>())
        {
            var clip = new TimelineClip { Kind = MediaKind.Image, SourcePath = imagePath, Out = 5, Framing = framing };
            var graphic = Path.Combine(root, $"framing-{framing}.png");
            await MediaEngine.RenderClipFrameAsync(clip, graphic, 0, CancellationToken.None);
            var rgb = Path.Combine(root, $"framing-{framing}.rgb");
            result = await MediaEngine.RunAsync("ffmpeg", ["-hide_banner", "-loglevel", "error", "-y", "-i", graphic, "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", rgb]);
            if (result.ExitCode != 0) throw new Exception(result.Error);
            var bytes = await File.ReadAllBytesAsync(rgb);
            var edge = (240 * 854 + 5) * 3;
            if (bytes.Length != 854 * 480 * 3 || (framing == ClipFraming.Fit ? bytes[edge] > 12 : bytes[edge] < 200)) throw new Exception($"Incorrect {framing} framing.");
        }
        var colorOnly = new MovieProject { Clips = [new() { Kind = MediaKind.Color, Out = 5, Color = "102030" }] };
        ProjectDocument.Validate(ProjectDocument.Clone(colorOnly));
        if (await MediaEngine.MaxSourceHeightAsync(colorOnly) != 2160) throw new Exception("Generated colors should allow 4K output without a source file.");
        foreach (var look in Enum.GetValues<ColorLook>())
            await MediaEngine.RenderClipFrameAsync(new() { SourcePath = video.Path, Out = 1, Look = look, Brightness = .05, Contrast = 1.2, Saturation = .8, Warmth = -.2 }, Path.Combine(root, $"look-{look}.jpg"), .3, CancellationToken.None);
        var mixed = new MovieProject { Assets = [video, image], Clips = [
            new() { Kind = MediaKind.Color, Out = .8 },
            new() { Kind = MediaKind.Image, SourcePath = imagePath, Out = 1, Framing = ClipFraming.Fill, TransitionIn = TransitionKind.Crossfade, TransitionDuration = .2 },
            new() { SourcePath = video.Path, Out = 1, Look = ColorLook.Cinema, TransitionIn = TransitionKind.Crossfade, TransitionDuration = .2 }],
            Titles = [new() { Preset = TitlePreset.Caption, Text = "Photo and color", Duration = 1.5, FadeIn = true }] };
        foreach (var preset in new[] { TitlePreset.Caption, TitlePreset.Corner, TitlePreset.Cinema })
            await MediaEngine.RenderTitleGraphicAsync(new() { Preset = preset, Text = "A new style", Shadow = true }, 640, 360, Path.Combine(root, $"title-{preset}.png"));
        var output = Path.Combine(root, "mixed-beta9.mp4");
        await MediaEngine.ExportAsync(mixed, output, new(640, 360, "Software (H.264)", Preview: true), (_, _) => { }, CancellationToken.None);
        var info = await MediaEngine.ProbeAsync(output);
        if (!info.HasAudio || Math.Abs(info.Duration - new TimelineLayout(mixed.Clips).Duration) > .12) throw new Exception("Mixed photo/color/video timing changed during export.");
        Console.WriteLine("PASS: still import, Fit/Fill/Stretch pixels, color-only resolution, all color looks, new title styles, mixed transitions/export");

        var original = new TimelineClip { SourcePath = video.Path, In = .5, Out = 2.5, Speed = 1.25 };
        var wav = Path.Combine(root, "extracted.wav");
        await MediaEngine.ExtractWaveAsync(original, wav, CancellationToken.None);
        var metadata = await MediaEngine.RunAsync("ffprobe", ["-v", "error", "-show_streams", "-of", "json", wav]);
        using (var json = JsonDocument.Parse(metadata.Output))
        {
            var stream = json.RootElement.GetProperty("streams")[0];
            if (stream.GetProperty("codec_name").GetString() != "pcm_s24le" || stream.GetProperty("sample_rate").GetString() != "48000") throw new Exception("Extracted WAV is not 48 kHz PCM.");
        }
        if (Math.Abs(await MediaEngine.ProbeAudioDurationAsync(wav) - original.Duration) > .05) throw new Exception("WAV range or speed does not match its clip.");
        var replacement = new MusicClip { Path = wav, Out = original.Duration, LinkedClipId = original.Id, LinkedSourceIn = original.In, LinkedSourceSpeed = original.Speed };
        var linked = new MovieProject { Assets = [video], Clips = [new() { Kind = MediaKind.Color, Out = .5 }, original], Music = [replacement] };
        original.Volume = 0;
        if (AudioPlacement.Resolve(linked, replacement).Start != .5) throw new Exception("Replacement audio did not follow its clip.");
        linked.Clips.Reverse();
        if (AudioPlacement.Resolve(linked, replacement).Start != 0) throw new Exception("Reordered replacement audio did not follow its clip.");
        original.In = .75; original.Speed = 2.5;
        var placement = AudioPlacement.Resolve(linked, replacement);
        if (Math.Abs(placement.In - .2) > .001 || placement.Speed != 2) throw new Exception("Replacement audio did not follow a trim or speed change.");
        replacement.SyncOffset = .1;
        linked.Music.Add(new MusicClip { Path = wav, Start = .1, Out = 1, Volume = .1, FadeIn = true, FadeOut = true, Normalize = true });
        await MediaEngine.ExportAsync(linked, Path.Combine(root, "linked-audio.mp4"), new(320, 180, "Software (H.264)"), (_, _) => { }, CancellationToken.None);
        // Extending before or after an extracted range must produce silence for unavailable sound, not fail.
        original.In = 0; original.Out = 3; original.Speed = 1;
        await MediaEngine.ExportAsync(linked, Path.Combine(root, "linked-padded.mp4"), new(320, 180, "Software (H.264)"), (_, _) => { }, CancellationToken.None);
        Console.WriteLine("PASS: PCM WAV extraction, replacement audio follows reorder/trim/speed, offset, overlapping tracks, normalization/fades, and padded missing audio");
    }
}
