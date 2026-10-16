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
    await MediaEngine.ExportAsync(project, output, "Software (H.264)",
        (fraction, message) => Console.WriteLine($"{fraction:P0} {message}"), CancellationToken.None);
    var result = await MediaEngine.ProbeAsync(output);
    if (!File.Exists(output) || result.Duration < 5 || result.Duration > 8 || !result.HasAudio)
        throw new Exception($"Invalid output: duration={result.Duration}, audio={result.HasAudio}");
    Console.WriteLine($"PASS: MP4 with video/audio, {result.Duration:0.00}s, {new FileInfo(output).Length:N0} bytes");
}
finally
{
    try { Directory.Delete(root, true); } catch { }
}
