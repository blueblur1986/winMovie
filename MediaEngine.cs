using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace WinMovie;

public sealed record VideoResolution(string Label, int Width, int Height);
public sealed record ExportSettings(int Width, int Height, string Encoder, bool Preview = false);

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
        var video = streams.First(x => x.GetProperty("codec_type").GetString() == "video");
        return new MediaAsset { Path = path, Duration = duration,
            Width = video.GetProperty("width").GetInt32(), Height = video.GetProperty("height").GetInt32(),
            HasAudio = streams.Any(x => x.GetProperty("codec_type").GetString() == "audio") };
    }

    public static async Task<double> ProbeAudioDurationAsync(string path)
    {
        var result = await RunAsync("ffprobe", ["-v", "error", "-show_format", "-show_streams", "-of", "json", path]);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.Error);
        using var json = JsonDocument.Parse(result.Output);
        if (!json.RootElement.GetProperty("streams").EnumerateArray().Any(x => x.GetProperty("codec_type").GetString() == "audio"))
            throw new InvalidOperationException("This file has no audio stream.");
        var value = json.RootElement.GetProperty("format").GetProperty("duration").GetString();
        if (!double.TryParse(value, NumberStyles.Float, C, out var duration) || duration <= 0)
            throw new InvalidOperationException("Could not read the audio duration.");
        return duration;
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

    public static async Task<string> DetectBestEncoderAsync(CancellationToken token = default)
    {
        var available = await AvailableEncodersAsync();
        foreach (var encoder in new[] { "NVIDIA NVENC", "Intel Quick Sync", "AMD AMF" })
        {
            if (!available.Contains(encoder)) continue;
            var probe = await RunAsync("ffmpeg", ["-hide_banner", "-loglevel", "error", "-f", "lavfi",
                "-i", "color=c=black:s=128x72:r=30:d=0.15", "-frames:v", "3", "-c:v", EncoderName(encoder),
                "-f", "null", "-"], token);
            if (probe.ExitCode == 0) return encoder;
        }
        return "Software (H.264)";
    }

    public static async Task<int> MaxSourceHeightAsync(MovieProject project)
    {
        var smallest = int.MaxValue;
        foreach (var path in project.Clips.Select(x => x.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var asset = project.Assets.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
            if (asset is null || asset.Height <= 0)
            {
                var probed = await ProbeAsync(path);
                if (asset is not null) { asset.Width = probed.Width; asset.Height = probed.Height; asset.HasAudio = probed.HasAudio; }
                else project.Assets.Add(probed);
                asset ??= probed;
            }
            smallest = Math.Min(smallest, asset.Height);
        }
        return smallest == int.MaxValue ? 0 : smallest;
    }

    public static IReadOnlyList<VideoResolution> ResolutionOptions(int sourceHeight)
    {
        var standards = new[] { new VideoResolution("480p", 854, 480), new VideoResolution("720p", 1280, 720),
            new VideoResolution("1080p", 1920, 1080), new VideoResolution("1440p", 2560, 1440),
            new VideoResolution("4K", 3840, 2160) };
        var options = standards.Where(x => x.Height <= sourceHeight).ToList();
        if (options.Count == 0 && sourceHeight > 0)
        {
            var height = Math.Max(2, sourceHeight / 2 * 2);
            var width = Math.Max(2, ((int)Math.Round(height * 16d / 9) / 2) * 2);
            options.Add(new VideoResolution($"Source limit ({height}p)", width, height));
        }
        return options;
    }

    public static async Task ExportAsync(MovieProject project, string output, ExportSettings settings,
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
                if (asset.Width <= 0 || asset.Height <= 0)
                {
                    var probed = await ProbeAsync(clip.SourcePath);
                    asset.Width = probed.Width; asset.Height = probed.Height; asset.HasAudio = probed.HasAudio;
                }
                var segment = System.IO.Path.Combine(temp, $"segment-{i:000}.mp4");
                await RenderSegmentAsync(clip, asset, segment, temp, settings, cancellationToken);
                segments.Add(segment);
                progress(.8 * (i + 1d) / project.Clips.Count, $"Preparing clip {i + 1} of {project.Clips.Count}");
            }
            progress(.82, "Encoding final video");
            await JoinAsync(project, segments, output, temp, settings, cancellationToken);
            progress(1, "Export complete");
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { /* Keep export error if cleanup fails. */ }
        }
    }

    private static async Task RenderSegmentAsync(TimelineClip clip, MediaAsset asset, string output,
        string temp, ExportSettings settings, CancellationToken token)
    {
        if (!File.Exists(clip.SourcePath)) throw new FileNotFoundException("Source media is missing", clip.SourcePath);
        var duration = clip.Duration;
        if (duration < .08) throw new InvalidOperationException("A timeline range is too short to export.");
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-ss", F(clip.In),
            "-t", F(clip.Out - clip.In), "-i", clip.SourcePath };
        if (!asset.HasAudio) args.AddRange(["-f", "lavfi", "-t", F(duration), "-i", "anullsrc=r=48000:cl=stereo"]);

        var boundedWidth = Math.Max(2, Math.Min(settings.Width, asset.Width) / 2 * 2);
        var boundedHeight = Math.Max(2, Math.Min(settings.Height, asset.Height) / 2 * 2);
        var video = $"[0:v]setpts=(PTS-STARTPTS)/{F(clip.Speed)},fps=30,scale={boundedWidth}:{boundedHeight}:force_original_aspect_ratio=decrease:force_divisible_by=2,pad={settings.Width}:{settings.Height}:(ow-iw)/2:(oh-ih)/2,setsar=1,format=yuv420p";
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
            "-c:v", "libx264", "-preset", settings.Preview ? "ultrafast" : "veryfast", "-crf", settings.Preview ? "30" : "18", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", settings.Preview ? "96k" : "192k", "-ar", "48000", "-ac", "2", "-shortest", output]);
        var result = await RunAsync("ffmpeg", args, token);
        if (result.ExitCode != 0) throw new InvalidOperationException("Could not prepare clip: " + result.Error);
    }

    private static async Task JoinAsync(MovieProject project, IReadOnlyList<string> segments,
        string output, string temp, ExportSettings settings, CancellationToken token)
    {
        var clips = project.Clips;
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        foreach (var segment in segments) args.AddRange(["-i", segment]);
        if (project.Music.Any(x => x.Duration >= .08 && !File.Exists(x.Path)))
            throw new FileNotFoundException("A background music file is missing.");
        var music = project.Music.Where(x => x.Duration >= .08).ToList();
        foreach (var track in music) args.AddRange(["-ss", F(track.In), "-t", F(track.Duration), "-i", track.Path]);
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
        var titleIndex = 0;
        foreach (var title in project.Titles.Where(x => x.Duration > .05 && !string.IsNullOrWhiteSpace(x.Text)))
        {
            var start = Math.Clamp(title.Start, 0, total);
            var end = Math.Clamp(title.Start + title.Duration, start, total);
            if (end <= start) continue;
            var textPath = System.IO.Path.Combine(temp, $"title-{titleIndex}.txt");
            await File.WriteAllTextAsync(textPath, title.Text, new UTF8Encoding(false), token);
            var fontFile = title.Font switch
            {
                "Arial" => "arial.ttf", "Georgia" => "georgia.ttf", "Consolas" => "consola.ttf",
                "Times New Roman" => "times.ttf", _ => "segoeui.ttf"
            };
            var fontPath = File.Exists(title.FontFile) ? title.FontFile :
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", fontFile);
            var area = title.Preset == TitlePreset.LowerThird ?
                ((int)(settings.Height * .72), (int)(settings.Height * .28), "h*0.79") :
                title.Preset == TitlePreset.Opening ?
                (0, settings.Height, "(h-text_h)/2") :
                ((int)(settings.Height * .35), (int)(settings.Height * .30), "(h-text_h)/2");
            var enable = $"between(t,{F(start)},{F(end)})";
            var background = SafeColor(title.BackgroundColor);
            var foreground = SafeColor(title.Color);
            var opacity = Math.Clamp(title.BackgroundOpacity, 0, 1);
            graph.Append($"color=c=black:s={settings.Width}x{settings.Height}:r=30:d={F(total)},format=rgba,colorchannelmixer=aa=0[tbase{titleIndex}];");
            var titleLayer = $"[tbase{titleIndex}]";
            if (opacity > .001)
            {
                graph.Append($"{titleLayer}drawbox=x=0:y={area.Item1}:w=iw:h={area.Item2}:color=0x{background}@{F(opacity)}:t=fill:replace=1[tbg{titleIndex}];");
                titleLayer = $"[tbg{titleIndex}]";
            }
            var size = Math.Max(12, (int)Math.Round(Math.Clamp(title.FontSize, 16, 160) * settings.Height / 1080d));
            var shadow = title.Shadow ? ":shadowcolor=black@0.9:shadowx=2:shadowy=2" : "";
            var outline = title.Outline ? ":borderw=2:bordercolor=black" : "";
            graph.Append($"{titleLayer}drawtext=fontfile='{FilterPath(fontPath)}':textfile='{FilterPath(textPath)}':fontsize={size}:fontcolor=0x{foreground}:x=(w-text_w)/2:y={area.Item3}{shadow}{outline}[ttxt{titleIndex}];");
            titleLayer = $"[ttxt{titleIndex}]";
            if (title.FadeIn)
            {
                var fade = Math.Min(Math.Clamp(title.FadeInDuration, .05, 10), (end - start) / 2);
                graph.Append($"{titleLayer}fade=t=in:st={F(start)}:d={F(fade)}:alpha=1[tfi{titleIndex}];");
                titleLayer = $"[tfi{titleIndex}]";
            }
            if (title.FadeOut)
            {
                var fade = Math.Min(Math.Clamp(title.FadeOutDuration, .05, 10), (end - start) / 2);
                graph.Append($"{titleLayer}fade=t=out:st={F(end - fade)}:d={F(fade)}:alpha=1[tfo{titleIndex}];");
                titleLayer = $"[tfo{titleIndex}]";
            }
            graph.Append($"{video}{titleLayer}overlay=enable='{enable}':shortest=1:format=auto[t{titleIndex}];");
            video = $"[t{titleIndex}]";
            titleIndex++;
        }
        for (var i = 0; i < music.Count; i++)
        {
            var track = music[i];
            var duration = Math.Min(track.Duration, Math.Max(0, total - track.Start));
            if (duration <= .05) continue;
            var fade = Math.Min(Math.Clamp(track.FadeOutDuration, .05, 30), duration);
            var musicFilter = $"[{segments.Count + i}:a]aresample=48000,atrim=duration={F(duration)},asetpts=PTS-STARTPTS,volume={F(Math.Clamp(track.Volume, 0, 4))}";
            if (track.FadeOut) musicFilter += $",afade=t=out:st={F(duration - fade)}:d={F(fade)}";
            var delay = (int)Math.Round(Math.Max(0, track.Start) * 1000);
            musicFilter += $",adelay={delay}|{delay}[music{i}]";
            graph.Append(musicFilter + ";");
            graph.Append($"{audio}[music{i}]amix=inputs=2:duration=first:dropout_transition=0[mix{i}];");
            audio = $"[mix{i}]";
        }
        if (graph.Length > 0) args.AddRange(["-filter_complex", graph.ToString().TrimEnd(';')]);
        args.AddRange(["-map", video.StartsWith("[0:") ? video.Trim('[', ']') : video,
            "-map", audio.StartsWith("[0:") ? audio.Trim('[', ']') : audio, "-c:v", EncoderName(settings.Encoder)]);
        if (settings.Encoder == "Software (H.264)") args.AddRange(["-preset", settings.Preview ? "ultrafast" : "medium", "-crf", settings.Preview ? "30" : "20"]);
        else if (settings.Encoder == "NVIDIA NVENC") args.AddRange(["-preset", "p4", "-cq", "22"]);
        else args.AddRange(["-b:v", "8M"]);
        args.AddRange(["-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", settings.Preview ? "96k" : "192k", "-movflags", "+faststart", output]);
        var result = await RunAsync("ffmpeg", args, token);
        if (result.ExitCode != 0 && settings.Encoder != "Software (H.264)")
        {
            // An encoder may be compiled into FFmpeg without a usable device or driver.
            await JoinAsync(project, segments, output, temp, settings with { Encoder = "Software (H.264)" }, token);
            return;
        }
        if (result.ExitCode != 0) throw new InvalidOperationException("MP4 export failed: " + result.Error);
    }

    private static string SafeColor(string? color) => color is { Length: 6 } && color.All(Uri.IsHexDigit) ? color : "FFFFFF";

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
