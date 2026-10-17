using WinMovie;

var root = Path.Combine(Path.GetTempPath(), "WinMovieSmoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var source = Path.Combine(root, "source.mp4");
    var output = Path.Combine(root, "output.mp4");
    var generated = await MediaEngine.RunAsync("ffmpeg", ["-hide_banner", "-loglevel", "error", "-y",
        "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30:duration=3",
        "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=3",
        "-c:v", "libx264", "-c:a", "aac", "-shortest", source]);
    if (generated.ExitCode != 0) throw new Exception(generated.Error);
    var asset = await MediaEngine.ProbeAsync(source);
    if (asset.Width != 640 || asset.Height != 360) throw new Exception("Source dimensions were not detected.");
    var sizes = MediaEngine.ResolutionOptions(asset.Height);
    if (sizes.Count != 1 || sizes[0].Width != 640 || sizes[0].Height != 360)
        throw new Exception("Small sources must not be offered an upscaled export size.");
    var detectedEncoder = await MediaEngine.DetectBestEncoderAsync();
    Console.WriteLine($"Detected encoder: {detectedEncoder}");
    var project = new MovieProject { Assets = [asset], Clips =
    [
        new TimelineClip { SourcePath = source, In = 0, Out = 1.6, FadeIn = true,
            TitlePreset = TitlePreset.Center, Title = "Hello WinMovie", Normalize = true },
        new TimelineClip { SourcePath = source, In = 1.2, Out = 3, Speed = 1.25,
            KeepPitch = true, Volume = 1.5, TransitionIn = TransitionKind.Crossfade, TransitionDuration = .7,
            TitlePreset = TitlePreset.LowerThird, Title = "Second clip", FadeOut = true },
        new TimelineClip { SourcePath = source, In = 0.3, Out = 1.7, Speed = .75,
            KeepPitch = false, TransitionIn = TransitionKind.FadeThroughBlack },
        new TimelineClip { SourcePath = source, In = 0, Out = 1.5,
            TransitionIn = TransitionKind.WipeLeft, TransitionDuration = .8 },
        new TimelineClip { SourcePath = source, In = 1.5, Out = 3,
            TransitionIn = TransitionKind.FadeThroughWhite, TransitionDuration = .6 },
        new TimelineClip { SourcePath = source, In = 0, Out = 1.5,
            TransitionIn = TransitionKind.SlideRight, TransitionDuration = .4, FadeOut = true, FadeOutDuration = .5 }
    ] };
    await MediaEngine.ExportAsync(project, output, new ExportSettings(640, 360, "Software (H.264)"),
        (fraction, message) => Console.WriteLine($"{fraction:P0} {message}"), CancellationToken.None);
    var result = await MediaEngine.ProbeAsync(output);
    if (!File.Exists(output) || result.Duration < 5 || result.Duration > 8 || !result.HasAudio || result.Width != 640 || result.Height != 360)
        throw new Exception($"Invalid output: duration={result.Duration}, audio={result.HasAudio}");
    Console.WriteLine($"PASS: MP4 with video/audio, {result.Duration:0.00}s, {new FileInfo(output).Length:N0} bytes");
    var preview = Path.Combine(root, "preview.mp4");
    await MediaEngine.ExportAsync(new MovieProject { Assets = [asset], Clips = project.Clips.Take(2).ToList() },
        preview, new ExportSettings(854, 480, "Software (H.264)", Preview: true), (_, _) => { }, CancellationToken.None);
    var previewInfo = await MediaEngine.ProbeAsync(preview);
    if (previewInfo.Width != 854 || previewInfo.Height != 480 || previewInfo.Duration < 2)
        throw new Exception("Rendered timeline preview is invalid.");
    Console.WriteLine("PASS: low-resolution timeline preview with transition");
    var autoOutput = Path.Combine(root, "automatic-encoder.mp4");
    await MediaEngine.ExportAsync(new MovieProject { Assets = [asset], Clips = project.Clips.Take(2).ToList() },
        autoOutput, new ExportSettings(640, 360, detectedEncoder), (_, _) => { }, CancellationToken.None);
    var autoInfo = await MediaEngine.ProbeAsync(autoOutput);
    if (autoInfo.Width != 640 || autoInfo.Height != 360 || !autoInfo.HasAudio)
        throw new Exception("Automatically selected encoder did not produce a valid MP4.");
    Console.WriteLine("PASS: automatic encoder MP4 export");
    var musicPath = Path.Combine(root, "music.wav");
    var musicGenerated = await MediaEngine.RunAsync("ffmpeg", ["-hide_banner", "-loglevel", "error", "-y",
        "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=4", musicPath]);
    if (musicGenerated.ExitCode != 0) throw new Exception(musicGenerated.Error);
    if (await MediaEngine.ProbeAudioDurationAsync(musicPath) < 3.9) throw new Exception("Music duration was not detected.");
    var layered = new MovieProject { Assets = [asset], Clips = project.Clips.Take(2).ToList(),
        Titles = [new TitleItem { Start = .8, Duration = 1.9, Text = "Across both clips", Preset = TitlePreset.LowerThird,
            Font = "Georgia", Color = "FFD34E", BackgroundColor = "000000", BackgroundOpacity = .65,
            Shadow = true, ShadowDistance = 5, ShadowBlur = 8, ShadowAngle = 45, ShadowOpacity = .4,
            Outline = true, OutlineThickness = 4, FadeIn = true, FadeOut = true, FadeInDuration = .35, FadeOutDuration = .45 }],
        Music = [new MusicClip { Path = musicPath, Start = .2, In = .1, Out = 1.7, Volume = .4,
            FadeOut = true, FadeOutDuration = 1.1 }] };
    var layeredOutput = Path.Combine(root, "layered.mp4");
    var graphic = Path.Combine(root, "title.png");
    await MediaEngine.RenderTitleGraphicAsync(layered.Titles[0], 640, 360, graphic);
    var rgba = Path.Combine(root, "title.rgba");
    var pixels = await MediaEngine.RunAsync("ffmpeg", ["-hide_banner", "-loglevel", "error", "-y", "-i", graphic,
        "-frames:v", "1", "-vf", "format=rgba", "-f", "rawvideo", rgba]);
    if (pixels.ExitCode != 0) throw new Exception(pixels.Error);
    var bytes = await File.ReadAllBytesAsync(rgba);
    if (bytes.Length != 640 * 360 * 4 || bytes[3] != 0 || !Enumerable.Range(0, bytes.Length / 4).Any(i => bytes[i * 4 + 3] > 0))
        throw new Exception("Title graphic must have transparent corners and visible content.");
    Console.WriteLine("PASS: transparent title graphic with blurred shadow and outline");
    await MediaEngine.ExportAsync(layered, layeredOutput, new ExportSettings(640, 360, "Software (H.264)"),
        (_, _) => { }, CancellationToken.None);
    var layeredInfo = await MediaEngine.ProbeAsync(layeredOutput);
    if (!layeredInfo.HasAudio || layeredInfo.Duration < 2 || layeredInfo.Width != 640)
        throw new Exception("Title and music track render is invalid.");
    Console.WriteLine("PASS: timed title across clips and fading music track");
}
finally
{
    try { Directory.Delete(root, true); } catch { }
}
