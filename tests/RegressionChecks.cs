using System.Globalization;
using System.Text.RegularExpressions;
using WinMovie;

internal static class RegressionChecks
{
    public static void CheckEditingState()
    {
        foreach (var json in new[] { "null", "{\"Clips\":null}", "{\"Clips\":[{\"SourcePath\":\"x\",\"Out\":1,\"Speed\":0}]}" })
        {
            try { ProjectDocument.Parse(json); throw new Exception("Invalid project was accepted."); }
            catch (InvalidDataException) { }
        }
        var project = new MovieProject { Clips = [
            new() { SourcePath = "x", Out = 2 },
            new() { SourcePath = "x", Out = 4, Speed = 2, TransitionIn = TransitionKind.Crossfade, TransitionDuration = .5 },
            new() { SourcePath = "x", Out = 1 }] };
        var layout = new TimelineLayout(project.Clips);
        if (layout.Starts[1] != 1.5 || layout.Starts[2] != 3.5 || layout.Duration != 4.5 || layout.ClipAt(project.Clips, 1.75) != 1)
            throw new Exception("Transition timing is inconsistent.");
        project.Clips[1].Title = "Legacy title"; project.Clips[1].TitlePreset = TitlePreset.Center;
        ProjectDocument.MigrateLegacyTitles(project);
        if (project.Titles.Count != 1 || project.Titles[0].Start != 1.5 || project.Clips[1].TitlePreset != TitlePreset.None)
            throw new Exception("Legacy title migration duplicated or misplaced a title.");
        var history = new EditHistory(); history.Reset("A"); history.Record("B"); history.Record("C");
        if (history.Undo() != "B" || history.Redo() != "C") throw new Exception("Undo/redo failed.");
        history.Undo(); history.Record("D");
        if (history.CanRedo || history.Undo() != "B") throw new Exception("Editing after undo did not replace the redo branch.");
        Console.WriteLine("PASS: project validation, shared transition timing, legacy title migration, undo/redo branches");
    }

    public static async Task CheckMediaAsync(string root, MediaAsset source, string music)
    {
        var settings = new ExportSettings(320, 180, "Software (H.264)", Preview: true);
        try
        {
            await MediaEngine.ExportAsync(new MovieProject { Assets = [source], Clips = [new() { SourcePath = source.Path, Out = 3 }] },
                source.Path, settings, (_, _) => { }, CancellationToken.None);
            throw new Exception("Export was allowed to replace a source file.");
        }
        catch (InvalidOperationException) { }
        var shortAudio = Path.Combine(root, "short-audio.mp4");
        var generated = await MediaEngine.RunAsync("ffmpeg", ["-hide_banner", "-loglevel", "error", "-y", "-i", source.Path,
            "-map", "0:v", "-map", "0:a", "-c:v", "copy", "-af", "atrim=duration=0.5", "-c:a", "aac", shortAudio]);
        if (generated.ExitCode != 0) throw new Exception(generated.Error);
        var asset = await MediaEngine.ProbeAsync(shortAudio);
        var padded = Path.Combine(root, "padded.mp4");
        await MediaEngine.ExportAsync(new MovieProject { Assets = [asset], Clips = [new() { SourcePath = shortAudio, Out = 3 }] },
            padded, settings, (_, _) => { }, CancellationToken.None);
        var paddedInfo = await MediaEngine.ProbeAsync(padded);
        if (paddedInfo.Duration < 2.95) throw new Exception("Short audio truncated the video.");
        Console.WriteLine("PASS: video duration survives a source audio stream that ends early");

        var clip = new TimelineClip { SourcePath = source.Path, Out = 3, Normalize = true };
        var project = new MovieProject { Assets = [source], Clips = [clip] };
        var full = Path.Combine(root, "normalized-full.mp4");
        var half = Path.Combine(root, "normalized-half.mp4");
        await MediaEngine.ExportAsync(project, full, settings, (_, _) => { }, CancellationToken.None);
        clip.Volume = .5;
        await MediaEngine.ExportAsync(project, half, settings, (_, _) => { }, CancellationToken.None);
        var difference = await VolumeDbAsync(half, .2, .5) - await VolumeDbAsync(full, .2, .5);
        if (difference < -7 || difference > -5) throw new Exception($"Normalization erased volume control: {difference:0.0} dB.");
        Console.WriteLine("PASS: normalization preserves the requested volume adjustment");

        clip.Volume = 1;
        project.Music.Add(new MusicClip { Path = music, Start = 1.5, Out = 1, Volume = .1 });
        var mixed = Path.Combine(root, "delayed-music.mp4");
        await MediaEngine.ExportAsync(project, mixed, settings, (_, _) => { }, CancellationToken.None);
        var levelDifference = await VolumeDbAsync(mixed, .2, .5) - await VolumeDbAsync(full, .2, .5);
        if (Math.Abs(levelDifference) > 1) throw new Exception($"Delayed music changed the original audio level: {levelDifference:0.0} dB.");
        Console.WriteLine("PASS: adding delayed music does not reduce original audio during silence");

        var title = new TitleItem { Text = "100% yours: [a title]", BackgroundOpacity = 0 };
        await MediaEngine.RenderTitleGraphicAsync(title, 320, 180, Path.Combine(root, "literal-title.png"));
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));
        var stopped = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await MediaEngine.RunAsync("ffmpeg", ["-hide_banner", "-loglevel", "error", "-re", "-f", "lavfi", "-i", "color=s=64x64:r=30", "-f", "null", "-"], cancelled.Token);
            throw new Exception("Cancelled rendering continued running.");
        }
        catch (OperationCanceledException) { }
        if (stopped.Elapsed > TimeSpan.FromSeconds(5)) throw new Exception("Cancellation did not stop the encoder promptly.");
        Console.WriteLine("PASS: literal title punctuation and encoder cancellation");
    }

    private static async Task<double> VolumeDbAsync(string path, double start, double duration)
    {
        var result = await MediaEngine.RunAsync("ffmpeg", ["-hide_banner", "-ss", MediaEngine.F(start), "-t", MediaEngine.F(duration),
            "-i", path, "-vn", "-af", "volumedetect", "-f", "null", "-"]);
        var match = Regex.Match(result.Error, @"mean_volume:\s*(-?[\d.]+) dB");
        if (result.ExitCode != 0 || !match.Success) throw new Exception("Could not measure exported audio: " + result.Error);
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }
}
