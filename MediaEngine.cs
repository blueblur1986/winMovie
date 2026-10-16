using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace WinMovie;

public static class MediaEngine
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;
    public static string F(double number) => number.ToString("0.######", C);

    public static async Task<MediaAsset> ProbeAsync(string path)
    {
        var result = await RunAsync("ffprobe", ["-v", "error", "-show_format", "-show_streams", "-of", "json", path]);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.Error);
        using var json = JsonDocument.Parse(result.Output);
        var root = json.RootElement;
        var streams = root.GetProperty("streams").EnumerateArray().ToArray();
        if (!streams.Any(x => x.GetProperty("codec_type").GetString() == "video"))
            throw new InvalidOperationException("This file has no video stream.");
        var durationText = root.GetProperty("format").GetProperty("duration").GetString();
        if (!double.TryParse(durationText, NumberStyles.Float, C, out var duration) || duration <= 0)
            throw new InvalidOperationException("Could not read the video duration.");
        return new MediaAsset { Path = path, Duration = duration,
            HasAudio = streams.Any(x => x.GetProperty("codec_type").GetString() == "audio") };
    }

    public static async Task<string> ThumbnailAsync(string path, double time, string directory)
    {
        Directory.CreateDirectory(directory);
        var image = System.IO.Path.Combine(directory, Guid.NewGuid().ToString("N") + ".jpg");
        var result = await RunAsync("ffmpeg", ["-hide_banner", "-loglevel", "error", "-y", "-ss", F(time),
            "-i", path, "-frames:v", "1", "-vf", "scale=320:-2", image]);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.Error);
        return image;
    }

    public static async Task<IReadOnlyList<string>> AvailableEncodersAsync()
    {
        var result = await RunAsync("ffmpeg", ["-hide_banner", "-encoders"]);
        var encoders = new List<string> { "Software (H.264)" };
        if (result.Output.Contains("h264_nvenc")) encoders.Add("NVIDIA NVENC");
        if (result.Output.Contains("h264_qsv")) encoders.Add("Intel Quick Sync");
        if (result.Output.Contains("h264_amf")) encoders.Add("AMD AMF");
        return encoders;
    }

    public static async Task ExportAsync(MovieProject project, string output, string encoder,
        Action<double, string> progress, CancellationToken cancellationToken)
    {
        if (project.Clips.Count == 0) throw new InvalidOperationException("Add a clip to the timeline first.");
        var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WinMovie-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var segments = new List<string>();
            for (var i = 0; i < project.Clips.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var clip = project.Clips[i];
                var asset = project.Assets.FirstOrDefault(x => x.Path == clip.SourcePath)
                    ?? await ProbeAsync(clip.SourcePath);
                var segment = System.IO.Path.Combine(temp, $"segment-{i:000}.mp4");
                await RenderSegmentAsync(clip, asset, segment, temp, cancellationToken);
                segments.Add(segment);
                progress((i + 1d) / (project.Clips.Count + 1), $"Preparing clip {i + 1} of {project.Clips.Count}");
            }
            await JoinAsync(project.Clips, segments, output, encoder, cancellationToken);
            progress(1, "Export complete");
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { /* Keep export error if cleanup fails. */ }
        }
    }

    private static async Task RenderSegmentAsync(TimelineClip clip, MediaAsset asset, string output,
        string temp, CancellationToken token)
    {
        if (!File.Exists(clip.SourcePath)) throw new FileNotFoundException("Source media is missing", clip.SourcePath);
        var duration = clip.Duration;
        if (duration < .08) throw new InvalidOperationException("A timeline range is too short to export.");
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-ss", F(clip.In),
            "-t", F(clip.Out - clip.In), "-i", clip.SourcePath };
        if (!asset.HasAudio) args.AddRange(["-f", "lavfi", "-t", F(duration), "-i", "anullsrc=r=48000:cl=stereo"]);

        var video = $"[0:v]setpts=(PTS-STARTPTS)/{F(clip.Speed)},fps=30,scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2,setsar=1,format=yuv420p";
        var fadeIn = Math.Clamp(clip.FadeInDuration, .05, Math.Max(.05, duration / 2));
        var fadeOut = Math.Clamp(clip.FadeOutDuration, .05, Math.Max(.05, duration / 2));
        if (clip.FadeIn) video += $",fade=t=in:st=0:d={F(fadeIn)}";
        if (clip.FadeOut) video += $",fade=t=out:st={F(Math.Max(0, duration - fadeOut))}:d={F(fadeOut)}";
        if (clip.TitlePreset != TitlePreset.None && !string.IsNullOrWhiteSpace(clip.Title))
        {
            var textPath = System.IO.Path.Combine(temp, Guid.NewGuid().ToString("N") + ".txt");
            await File.WriteAllTextAsync(textPath, clip.Title, new UTF8Encoding(false), token);
            var font = clip.TitlePreset == TitlePreset.Opening ? "segoeuib.ttf" : "segoeui.ttf";
            var fontPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", font);
            var size = clip.TitlePreset == TitlePreset.Opening ? 74 : clip.TitlePreset == TitlePreset.LowerThird ? 48 : 62;
            var x = clip.TitlePreset == TitlePreset.LowerThird ? "100" : "(w-text_w)/2";
            var y = clip.TitlePreset == TitlePreset.LowerThird ? "h-170" : "(h-text_h)/2";
            video += $",drawtext=fontfile='{FilterPath(fontPath)}':textfile='{FilterPath(textPath)}':fontsize={size}:fontcolor=white:shadowcolor=black@0.8:shadowx=3:shadowy=3:x={x}:y={y}";
        }
        video += "[v]";

        var audioInput = asset.HasAudio ? "[0:a]" : "[1:a]";
        var audio = audioInput + "aresample=48000,asetpts=PTS-STARTPTS";
        if (asset.HasAudio)
        {
            if (clip.KeepPitch) audio += "," + TempoFilters(clip.Speed);
            else audio += $",asetrate={F(48000 * clip.Speed)},aresample=48000";
            if (Math.Abs(clip.Volume - 1) > .001) audio += $",volume={F(clip.Volume)}";
            if (clip.Normalize) audio += ",loudnorm=I=-16:TP=-1.5:LRA=11";
        }
        audio += $",atrim=duration={F(duration)},asetpts=PTS-STARTPTS";
        if (clip.FadeIn) audio += $",afade=t=in:st=0:d={F(fadeIn)}";
        if (clip.FadeOut) audio += $",afade=t=out:st={F(Math.Max(0, duration - fadeOut))}:d={F(fadeOut)}";
        audio += "[a]";
        args.AddRange(["-filter_complex", video + ";" + audio, "-map", "[v]", "-map", "[a]",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2", "-shortest", output]);
        var result = await RunAsync("ffmpeg", args, token);
        if (result.ExitCode != 0) throw new InvalidOperationException("Could not prepare clip: " + result.Error);
    }

    private static async Task JoinAsync(IReadOnlyList<TimelineClip> clips, IReadOnlyList<string> segments,
        string output, string encoder, CancellationToken token)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        foreach (var segment in segments) args.AddRange(["-i", segment]);
        string video = "[0:v]", audio = "[0:a]";
        var total = clips[0].Duration;
        var graph = new StringBuilder();
        for (var i = 1; i < clips.Count; i++)
        {
            var nextV = $"[v{i}]";
            var nextA = $"[a{i}]";
            var overlap = clips[i].TransitionIn == TransitionKind.Cut ? 0 :
                Math.Min(Math.Clamp(clips[i].TransitionDuration, .05, 10), Math.Min(total, clips[i].Duration) / 2);
            if (overlap <= .02)
            {
                graph.Append($"{video}[{i}:v]concat=n=2:v=1:a=0{nextV};");
                graph.Append($"{audio}[{i}:a]concat=n=2:v=0:a=1{nextA};");
            }
            else
            {
                var mode = clips[i].TransitionIn switch
                {
                    TransitionKind.FadeThroughBlack => "fadeblack",
                    TransitionKind.FadeThroughWhite => "fadewhite",
                    TransitionKind.WipeLeft => "wipeleft",
                    TransitionKind.WipeRight => "wiperight",
                    TransitionKind.SlideLeft => "slideleft",
                    TransitionKind.SlideRight => "slideright",
                    _ => "fade"
                };
                graph.Append($"{video}[{i}:v]xfade=transition={mode}:duration={F(overlap)}:offset={F(total - overlap)}{nextV};");
                graph.Append($"{audio}[{i}:a]acrossfade=d={F(overlap)}{nextA};");
            }
            video = nextV;
            audio = nextA;
            total += clips[i].Duration - overlap;
        }
        if (clips.Count > 1) args.AddRange(["-filter_complex", graph.ToString().TrimEnd(';')]);
        args.AddRange(["-map", clips.Count == 1 ? "0:v" : video,
            "-map", clips.Count == 1 ? "0:a" : audio, "-c:v", EncoderName(encoder)]);
        if (encoder == "Software (H.264)") args.AddRange(["-preset", "medium", "-crf", "20"]);
        else if (encoder == "NVIDIA NVENC") args.AddRange(["-preset", "p4", "-cq", "22"]);
        else args.AddRange(["-b:v", "8M"]);
        args.AddRange(["-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", output]);
        var result = await RunAsync("ffmpeg", args, token);
        if (result.ExitCode != 0 && encoder != "Software (H.264)")
        {
            // An encoder may be compiled into FFmpeg without a usable device or driver.
            await JoinAsync(clips, segments, output, "Software (H.264)", token);
            return;
        }
        if (result.ExitCode != 0) throw new InvalidOperationException("MP4 export failed: " + result.Error);
    }

    private static string EncoderName(string selection) => selection switch
    {
        "NVIDIA NVENC" => "h264_nvenc",
        "Intel Quick Sync" => "h264_qsv",
        "AMD AMF" => "h264_amf",
        _ => "libx264"
    };

    private static string TempoFilters(double speed)
    {
        var filters = new List<string>();
        while (speed > 2) { filters.Add("atempo=2"); speed /= 2; }
        while (speed < .5) { filters.Add("atempo=0.5"); speed *= 2; }
        filters.Add("atempo=" + F(speed));
        return string.Join(',', filters);
    }

    private static string FilterPath(string path) => path.Replace('\\', '/').Replace(":", "\\:").Replace("'", "\\'");

    public static async Task<(int ExitCode, string Output, string Error)> RunAsync(string executable,
        IEnumerable<string> arguments, CancellationToken token = default)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not launch {executable}.");
        using var registration = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(token);
        return (process.ExitCode, await output, await error);
    }
}
