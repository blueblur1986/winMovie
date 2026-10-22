using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinMovie;

public sealed record VideoResolution(string Label, int Width, int Height);
public sealed record ExportSettings(int Width, int Height, string Encoder, bool Preview = false);

public static class MediaEngine
{
    public static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tif", ".tiff"];
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;
    private static readonly string PreviewSegmentDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WinMoviePreviewSegments");
    private static readonly SemaphoreSlim PreviewCacheGate = new(1, 1);
    private static bool _previewCachePruned;
    public static string F(double number) => number.ToString("0.######", C);

    public static async Task<MediaAsset> ProbeAsync(string path, CancellationToken token = default)
    {
        var result = await RunAsync("ffprobe", ["-v", "error", "-show_format", "-show_streams", "-of", "json", path], token);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.Error);
        using var json = JsonDocument.Parse(result.Output);
        var root = json.RootElement;
        var streams = root.GetProperty("streams").EnumerateArray().ToArray();
        if (!streams.Any(x => x.GetProperty("codec_type").GetString() == "video"))
            throw new InvalidOperationException("This file has no video stream.");
        var still = ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
        var durationText = root.GetProperty("format").TryGetProperty("duration", out var durationValue) ? durationValue.GetString() : null;
        double duration = 5;
        if (!still && (!double.TryParse(durationText, NumberStyles.Float, C, out duration) || duration <= 0))
            throw new InvalidOperationException("Could not read the video duration.");
        var video = streams.FirstOrDefault(x => x.GetProperty("codec_type").GetString() == "video" &&
            (!x.TryGetProperty("disposition", out var flags) || !flags.TryGetProperty("attached_pic", out var attached) || attached.GetInt32() == 0));
        if (video.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException("This file has no video stream.");
        var width = video.GetProperty("width").GetInt32();
        var height = video.GetProperty("height").GetInt32();
        if (video.TryGetProperty("side_data_list", out var sideData))
            foreach (var data in sideData.EnumerateArray())
                if (data.TryGetProperty("rotation", out var rotation) && Math.Abs(rotation.GetDouble()) % 180 == 90) (width, height) = (height, width);
        return new MediaAsset { Path = path, Duration = duration, Kind = still ? MediaKind.Image : MediaKind.Video,
            Width = width, Height = height,
            HasAudio = !still && streams.Any(x => x.GetProperty("codec_type").GetString() == "audio") };
    }

    public static async Task<double> ProbeAudioDurationAsync(string path, CancellationToken token = default)
    {
        var result = await RunAsync("ffprobe", ["-v", "error", "-show_format", "-show_streams", "-of", "json", path], token);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.Error);
        using var json = JsonDocument.Parse(result.Output);
        if (!json.RootElement.GetProperty("streams").EnumerateArray().Any(x => x.GetProperty("codec_type").GetString() == "audio"))
            throw new InvalidOperationException("This file has no audio stream.");
        var value = json.RootElement.GetProperty("format").GetProperty("duration").GetString();
        if (!double.TryParse(value, NumberStyles.Float, C, out var duration) || duration <= 0)
            throw new InvalidOperationException("Could not read the audio duration.");
        return duration;
    }

    public static async Task<string> ThumbnailAsync(string path, double time, string directory, CancellationToken token = default)
    {
        Directory.CreateDirectory(directory);
        var image = System.IO.Path.Combine(directory, Guid.NewGuid().ToString("N") + ".jpg");
        if (ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) time = 0;
        var result = await RunAsync("ffmpeg", ["-hide_banner", "-loglevel", "error", "-y", "-ss", F(time),
            "-i", path, "-frames:v", "1", "-vf", "scale=320:-2", image], token);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.Error);
        return image;
    }

    public static async Task<IReadOnlyList<string>> AvailableEncodersAsync(CancellationToken token = default)
    {
        var result = await RunAsync("ffmpeg", ["-hide_banner", "-encoders"], token);
        var encoders = new List<string> { "Software (H.264)" };
        if (result.Output.Contains("h264_nvenc")) encoders.Add("NVIDIA NVENC");
        if (result.Output.Contains("h264_qsv")) encoders.Add("Intel Quick Sync");
        if (result.Output.Contains("h264_amf")) encoders.Add("AMD AMF");
        return encoders;
    }

    public static async Task<string> DetectBestEncoderAsync(CancellationToken token = default)
    {
        var available = await AvailableEncodersAsync(token);
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
        foreach (var path in project.Clips.Where(x => x.Kind != MediaKind.Color).Select(x => x.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var asset = await ProbeAsync(path);
            smallest = Math.Min(smallest, asset.Height);
        }
        return smallest == int.MaxValue && project.Clips.Count > 0 ? 2160 : smallest == int.MaxValue ? 0 : smallest;
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
        project = ProjectDocument.Clone(project);
        if (project.Clips.Count == 0) throw new InvalidOperationException("Add a clip to the timeline first.");
        var destination = Path.GetFullPath(output);
        if (project.Clips.Any(x => x.Kind != MediaKind.Color && string.Equals(Path.GetFullPath(x.SourcePath), destination, StringComparison.OrdinalIgnoreCase)) ||
            project.Music.Any(x => string.Equals(Path.GetFullPath(x.Path), destination, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Export to a new filename to keep the original media.");
        if (settings.Width < 2 || settings.Height < 2 || settings.Width % 2 != 0 || settings.Height % 2 != 0)
            throw new ArgumentException("Choose an even export width and height of at least two pixels.");
        var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WinMovie-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            if (settings.Preview) PrunePreviewSegmentCache();
            var media = new Dictionary<string, MediaAsset>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in project.Clips.Where(x => x.Kind != MediaKind.Color).Select(x => x.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase))
                media[path] = await ProbeAsync(path, cancellationToken);
            foreach (var path in project.Music.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var length = await ProbeAudioDurationAsync(path, cancellationToken);
                if (project.Music.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase) && x.Out > length + .05))
                    throw new InvalidOperationException($"Audio has changed: {Path.GetFileName(path)} is now {F(length)} seconds long. Edit its trim before exporting.");
            }
            var segments = new List<string>();
            for (var i = 0; i < project.Clips.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var clip = project.Clips[i];
                var asset = clip.Kind == MediaKind.Color ? new MediaAsset { Width = settings.Width, Height = settings.Height } : media[clip.SourcePath];
                if (clip.Kind != MediaKind.Color && asset.Kind != clip.Kind) throw new InvalidOperationException($"Media type has changed: {clip.Name}. Relink the original file.");
                if (clip.Kind == MediaKind.Video && clip.Out > asset.Duration + .05)
                    throw new InvalidOperationException($"Video has changed: {Path.GetFileName(clip.SourcePath)} is now {F(asset.Duration)} seconds long. Shorten the clip trim before exporting.");
                var segment = System.IO.Path.Combine(temp, $"segment-{i:000}.mp4");
                if (settings.Preview) await RenderCachedPreviewSegmentAsync(clip, asset, segment, temp, settings, cancellationToken);
                else await RenderSegmentAsync(clip, asset, segment, temp, settings, cancellationToken);
                segments.Add(segment);
                progress(.8 * (i + 1d) / project.Clips.Count, $"Preparing clip {i + 1} of {project.Clips.Count}");
            }
            progress(.82, "Encoding final video");
            await JoinAsync(project, segments, output, temp, settings, progress, cancellationToken);
            progress(1, "Export complete");
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { /* Keep export error if cleanup fails. */ }
        }
    }

    private static async Task RenderCachedPreviewSegmentAsync(TimelineClip clip, MediaAsset asset,
        string output, string temp, ExportSettings settings, CancellationToken token)
    {
        if (clip.Kind == MediaKind.Color) { await RenderSegmentAsync(clip, asset, output, temp, settings, token); return; }
        try { Directory.CreateDirectory(PreviewSegmentDirectory); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        { await RenderSegmentAsync(clip, asset, output, temp, settings, token); return; }
        var source = new FileInfo(clip.SourcePath);
        var segmentSettings = JsonSerializer.Serialize(new { clip.Kind, clip.Color, clip.Framing, clip.Brightness, clip.Contrast, clip.Saturation, clip.Warmth, clip.Look, clip.In, clip.Out, clip.Speed, clip.KeepPitch, clip.Volume, clip.Normalize, clip.TitlePreset, clip.Title, clip.FadeIn, clip.FadeOut, clip.FadeInDuration, clip.FadeOutDuration });
        var identity = $"v3|{source.FullName}|{source.Length}|{source.LastWriteTimeUtc.Ticks}|{settings.Width}x{settings.Height}|{settings.Encoder}|{segmentSettings}";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var cached = System.IO.Path.Combine(PreviewSegmentDirectory, key + ".mp4");
        await PreviewCacheGate.WaitAsync(token);
        try
        {
            if (File.Exists(cached) && new FileInfo(cached).Length > 1024)
            {
                await CopyFileAsync(cached, output, token);
                try { File.SetLastWriteTimeUtc(cached, DateTime.UtcNow); } catch { }
                return;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { /* Render without using the cache. */ }
        finally { PreviewCacheGate.Release(); }
        await RenderSegmentAsync(clip, asset, output, temp, settings, token);
        await PreviewCacheGate.WaitAsync(token);
        var stagedCache = cached + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (!File.Exists(cached))
            {
                await CopyFileAsync(output, stagedCache, token);
                File.Move(stagedCache, cached);
                PrunePreviewSegmentCache(true);
            }
        }
        catch (IOException) { /* Keep the completed preview even when caching fails. */ }
        catch (UnauthorizedAccessException) { /* Keep the completed preview even when caching fails. */ }
        finally { try { File.Delete(stagedCache); } catch { } PreviewCacheGate.Release(); }
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken token)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await input.CopyToAsync(output, token);
    }

    private static void PrunePreviewSegmentCache(bool force = false)
    {
        if (_previewCachePruned && !force) return;
        _previewCachePruned = true;
        try
        {
            if (!Directory.Exists(PreviewSegmentDirectory)) return;
            long total = 0;
            foreach (var file in new DirectoryInfo(PreviewSegmentDirectory).GetFiles("*.mp4").OrderByDescending(x => x.LastWriteTimeUtc))
            {
                total += file.Length;
                if (total <= 512_000_000 && DateTime.UtcNow - file.LastWriteTimeUtc < TimeSpan.FromDays(7)) continue;
                try { file.Delete(); } catch { }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { /* Cache maintenance must not block preview. */ }
    }

    private static async Task RenderSegmentAsync(TimelineClip clip, MediaAsset asset, string output,
        string temp, ExportSettings settings, CancellationToken token)
    {
        if (clip.Kind != MediaKind.Color && !File.Exists(clip.SourcePath)) throw new FileNotFoundException("Source media is missing", clip.SourcePath);
        var duration = clip.Duration;
        if (duration < .08) throw new InvalidOperationException("A timeline range is too short to export.");
        var args = VisualInput(clip, settings.Width, settings.Height, clip.In);
        if (!asset.HasAudio) args.AddRange(["-f", "lavfi", "-t", F(duration), "-i", "anullsrc=r=48000:cl=stereo"]);

        var video = $"[0:V:0]setpts=(PTS-STARTPTS)/{F(clip.Speed)},fps=30," + AppearanceFilter(clip, asset, settings.Width, settings.Height);
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
            video += $",drawtext=expansion=none:fontfile='{FilterPath(fontPath)}':textfile='{FilterPath(textPath)}':fontsize={size}:fontcolor=white:shadowcolor=black@0.8:shadowx=3:shadowy=3:x={x}:y={y}";
        }
        video += "[v]";

        var audioInput = asset.HasAudio ? "[0:a]" : "[1:a]";
        var audio = audioInput + "aresample=48000,asetpts=PTS-STARTPTS";
        if (asset.HasAudio)
        {
            if (clip.KeepPitch) audio += "," + TempoFilters(clip.Speed);
            else audio += $",asetrate={F(48000 * clip.Speed)},aresample=48000";
            if (clip.Normalize) audio += ",loudnorm=I=-16:TP=-1.5:LRA=11";
            if (Math.Abs(clip.Volume - 1) > .001) audio += $",volume={F(clip.Volume)}";
        }
        audio += $",aresample=48000,apad,atrim=duration={F(duration)},asetpts=PTS-STARTPTS";
        if (clip.FadeIn) audio += $",afade=t=in:st=0:d={F(fadeIn)}";
        if (clip.FadeOut) audio += $",afade=t=out:st={F(Math.Max(0, duration - fadeOut))}:d={F(fadeOut)}";
        audio += "[a]";
        var encoder = EncoderName(settings.Encoder);
        args.AddRange(["-filter_complex", video + ";" + audio, "-map", "[v]", "-map", "[a]", "-c:v", encoder]);
        if (encoder == "libx264") args.AddRange(["-preset", settings.Preview ? "ultrafast" : "veryfast", "-crf", settings.Preview ? "30" : "18"]);
        else if (encoder == "h264_nvenc") args.AddRange(["-preset", settings.Preview ? "p1" : "p4", "-cq", settings.Preview ? "30" : "18"]);
        else args.AddRange(["-b:v", settings.Preview ? "2M" : "12M"]);
        args.AddRange(["-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", settings.Preview ? "96k" : "192k", "-ar", "48000", "-ac", "2", "-t", F(duration), "-shortest", output]);
        var result = await RunAsync("ffmpeg", args, token);
        if (result.ExitCode != 0 && encoder != "libx264")
        {
            await RenderSegmentAsync(clip, asset, output, temp, settings with { Encoder = "Software (H.264)" }, token);
            return;
        }
        if (result.ExitCode != 0) throw new InvalidOperationException("Could not prepare clip: " + result.Error);
    }

    public static async Task RenderTitleGraphicAsync(TitleItem title, int width, int height, string output,
        CancellationToken token = default)
    {
        if (width < 2 || height < 2) throw new ArgumentOutOfRangeException(nameof(width));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(output)!);
        var textPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(output)!, Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(textPath, title.Text, new UTF8Encoding(false), token);
        try
        {
            var fontFile = title.Font switch
            {
                "Arial" => "arial.ttf", "Georgia" => "georgia.ttf", "Consolas" => "consola.ttf",
                "Times New Roman" => "times.ttf", _ => "segoeui.ttf"
            };
            var fontPath = File.Exists(title.FontFile) ? title.FontFile :
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", fontFile);
            var area = title.Preset == TitlePreset.Corner ?
                ((int)(height * .05), (int)(height * .24), "h*0.10") :
                title.Preset is TitlePreset.Caption or TitlePreset.Cinema ?
                ((int)(height * .8), (int)(height * .2), "h*0.86-text_h/2") :
                title.Preset == TitlePreset.LowerThird ?
                ((int)(height * .72), (int)(height * .28), "h*0.79") :
                title.Preset == TitlePreset.Opening ?
                (0, height, "(h-text_h)/2") :
                ((int)(height * .35), (int)(height * .30), "(h-text_h)/2");
            var size = Math.Max(12, (int)Math.Round(Math.Clamp(title.FontSize, 16, 160) * height / 1080d));
            var drawText = $"expansion=none:fontfile='{FilterPath(fontPath)}':textfile='{FilterPath(textPath)}':fontsize={size}";
            var textX = title.Preset == TitlePreset.Corner ? "w*0.06" : "(w-text_w)/2";
            var graph = new StringBuilder("[0:v]format=rgba,colorchannelmixer=aa=0[clear];");
            var baseLayer = "[clear]";
            if (title.Shadow)
            {
                graph.Append("[clear]split=2[base][shadowbase];");
                baseLayer = "[base]";
            }
            var backgroundOpacity = Math.Clamp(title.BackgroundOpacity, 0, 1);
            if (title.Preset == TitlePreset.Cinema)
            {
                graph.Append($"{baseLayer}drawbox=x=0:y=0:w=iw:h=ih*0.16:color=0x{SafeColor(title.BackgroundColor)}@{F(backgroundOpacity)}:t=fill:replace=1,drawbox=x=0:y=ih*0.8:w=iw:h=ih*0.2:color=0x{SafeColor(title.BackgroundColor)}@{F(backgroundOpacity)}:t=fill:replace=1[cinema];");
                baseLayer = "[cinema]";
            }
            else if (backgroundOpacity > .001 && title.Preset != TitlePreset.Caption)
            {
                graph.Append($"{baseLayer}drawbox=x=0:y={area.Item1}:w=iw:h={area.Item2}:color=0x{SafeColor(title.BackgroundColor)}@{F(backgroundOpacity)}:t=fill:replace=1[background];");
                baseLayer = "[background]";
            }
            if (title.Shadow)
            {
                var radians = Math.Clamp(title.ShadowAngle, 0, 360) * Math.PI / 180;
                var distance = Math.Clamp(title.ShadowDistance, 0, 30) * height / 1080d;
                var dx = (int)Math.Round(distance * Math.Cos(radians));
                var dy = (int)Math.Round(distance * Math.Sin(radians));
                var x = $"{textX}{(dx >= 0 ? "+" : "")}{dx}";
                var y = $"{area.Item3}{(dy >= 0 ? "+" : "")}{dy}";
                graph.Append($"[shadowbase]drawtext={drawText}:fontcolor=black@{F(Math.Clamp(title.ShadowOpacity, 0, 1))}:x={x}:y={y}");
                var blur = Math.Clamp(title.ShadowBlur, 0, 30) * height / 1080d;
                if (blur > .1) graph.Append($",gblur=sigma={F(blur)}:planes=8");
                graph.Append("[shadow];");
                graph.Append($"{baseLayer}[shadow]overlay=shortest=1:format=auto[withshadow];");
                baseLayer = "[withshadow]";
            }
            var outline = title.Outline ? Math.Max(1, (int)Math.Round(Math.Clamp(title.OutlineThickness, 1, 16) * height / 1080d)) : 0;
            var caption = title.Preset == TitlePreset.Caption ? $":box=1:boxcolor=0x{SafeColor(title.BackgroundColor)}@{F(backgroundOpacity)}:boxborderw={Math.Max(3, size / 4)}" : "";
            graph.Append($"{baseLayer}drawtext={drawText}:fontcolor=0x{SafeColor(title.Color)}:x={textX}:y={area.Item3}:borderw={outline}:bordercolor=black@0.9{caption}[out]");
            var result = await RunAsync("ffmpeg", ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
                $"color=c=black:s={width}x{height}:r=1:d=1", "-filter_complex", graph.ToString(),
                "-map", "[out]", "-frames:v", "1", "-update", "1", "-pix_fmt", "rgba", output], token);
            if (result.ExitCode != 0) throw new InvalidOperationException("Could not render title: " + result.Error);
        }
        finally { try { File.Delete(textPath); } catch { } }
    }

    private static async Task JoinAsync(MovieProject project, IReadOnlyList<string> segments,
        string output, string temp, ExportSettings settings, Action<double, string> progress, CancellationToken token)
    {
        var clips = project.Clips;
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        foreach (var segment in segments) args.AddRange(["-i", segment]);
        if (project.Music.FirstOrDefault(x => x.Duration >= .08 && !File.Exists(x.Path)) is { } missing)
            throw new FileNotFoundException($"Audio file is missing: {missing.Path}", missing.Path);
        var music = project.Music.Where(x => x.Duration >= .08).ToList();
        var placements = music.Select(x => AudioPlacement.Resolve(project, x)).ToArray();
        foreach (var placement in placements.Select((value, index) => (value, index)))
        {
            var item = placement.value;
            if (item.ReadDuration > .001) args.AddRange(["-ss", F(item.In), "-t", F(item.ReadDuration), "-i", music[placement.index].Path]);
            else args.AddRange(["-f", "lavfi", "-t", F(item.Duration), "-i", "anullsrc=r=48000:cl=stereo"]);
        }
        string video = "[0:v]", audio = "[0:a]";
        var layout = new TimelineLayout(clips);
        var total = clips[0].Duration;
        var graph = new StringBuilder();
        for (var i = 1; i < clips.Count; i++)
        {
            var nextV = $"[v{i}]";
            var nextA = $"[a{i}]";
            var overlap = layout.Overlaps[i];
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
            var graphic = System.IO.Path.Combine(temp, $"title-{titleIndex}.png");
            await RenderTitleGraphicAsync(title, settings.Width, settings.Height, graphic, token);
            var inputIndex = segments.Count + music.Count + titleIndex;
            args.AddRange(["-loop", "1", "-framerate", "30", "-i", graphic]);
            var enable = $"between(t,{F(start)},{F(end)})";
            graph.Append($"[{inputIndex}:v]format=rgba[tbase{titleIndex}];");
            var titleLayer = $"[tbase{titleIndex}]";
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
            var placement = placements[i];
            var duration = Math.Min(placement.Duration, Math.Max(0, total - placement.Start));
            if (duration <= .05) continue;
            var fade = Math.Min(Math.Clamp(track.FadeOutDuration, .05, 30), duration);
            var musicFilter = $"[{segments.Count + i}:a]aresample=48000,asetpts=PTS-STARTPTS,{TempoFilters(placement.Speed)}";
            if (track.Normalize) musicFilter += ",loudnorm=I=-16:TP=-1.5:LRA=11";
            musicFilter += $",volume={F(Math.Clamp(track.Volume, 0, 4))}";
            if (placement.LeadingSilence > 0) musicFilter += $",adelay={F(placement.LeadingSilence * 1000)}:all=1";
            musicFilter += $",apad,atrim=duration={F(duration)}";
            if (track.FadeIn) musicFilter += $",afade=t=in:st=0:d={F(Math.Min(track.FadeInDuration, duration))}";
            if (track.FadeOut) musicFilter += $",afade=t=out:st={F(duration - fade)}:d={F(fade)}";
            var delay = (int)Math.Round(Math.Max(0, placement.Start) * 1000);
            musicFilter += $",adelay={delay}|{delay}[music{i}]";
            graph.Append(musicFilter + ";");
            graph.Append($"{audio}[music{i}]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[mix{i}];");
            audio = $"[mix{i}]";
        }
        if (music.Count > 0) { graph.Append($"{audio}alimiter=limit=0.95:level=false[limited];"); audio = "[limited]"; }
        if (graph.Length > 0) args.AddRange(["-filter_complex", graph.ToString().TrimEnd(';')]);
        args.AddRange(["-map", video.StartsWith("[0:") ? video.Trim('[', ']') : video,
            "-map", audio.StartsWith("[0:") ? audio.Trim('[', ']') : audio, "-c:v", EncoderName(settings.Encoder)]);
        if (settings.Encoder == "Software (H.264)") args.AddRange(["-preset", settings.Preview ? "ultrafast" : "medium", "-crf", settings.Preview ? "30" : "20"]);
        else if (settings.Encoder == "NVIDIA NVENC") args.AddRange(["-preset", "p4", "-cq", "22"]);
        else args.AddRange(["-b:v", "8M"]);
        args.AddRange(["-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", settings.Preview ? "96k" : "192k", "-movflags", "+faststart", "-t", F(layout.Duration), "-progress", "pipe:1", "-nostats", output]);
        var result = await RunAsync("ffmpeg", args, token, line =>
        {
            if (line.StartsWith("out_time_us=") && double.TryParse(line[12..], NumberStyles.Float, C, out var microseconds))
                progress(.82 + .17 * Math.Clamp(microseconds / 1_000_000 / Math.Max(.01, layout.Duration), 0, 1), "Encoding final video");
        });
        if (result.ExitCode != 0 && settings.Encoder != "Software (H.264)")
        {
            // An encoder may be compiled into FFmpeg without a usable device or driver.
            await JoinAsync(project, segments, output, temp, settings with { Encoder = "Software (H.264)" }, progress, token);
            return;
        }
        if (result.ExitCode != 0) throw new InvalidOperationException("MP4 export failed: " + result.Error);
    }

    private static List<string> VisualInput(TimelineClip clip, int width, int height, double time)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        if (clip.Kind == MediaKind.Color)
            args.AddRange(["-f", "lavfi", "-i", $"color=c=0x{SafeColor(clip.Color)}:s={width}x{height}:r=30"]);
        else if (clip.Kind == MediaKind.Image) args.AddRange(["-loop", "1", "-framerate", "30", "-i", clip.SourcePath]);
        else args.AddRange(["-ss", F(time), "-t", F(clip.Out - time), "-i", clip.SourcePath]);
        return args;
    }

    public static string AppearanceFilter(TimelineClip clip, MediaAsset asset, int width, int height)
    {
        var boundedWidth = Math.Max(2, Math.Min(width, asset.Width) / 2 * 2);
        var boundedHeight = Math.Max(2, Math.Min(height, asset.Height) / 2 * 2);
        var sizing = clip.Kind == MediaKind.Color ? $"scale={width}:{height}" : clip.Framing switch
        {
            ClipFraming.Fill => $"scale={width}:{height}:force_original_aspect_ratio=increase:force_divisible_by=2,crop={width}:{height}",
            ClipFraming.Stretch => $"scale={width}:{height}",
            _ => $"scale={boundedWidth}:{boundedHeight}:force_original_aspect_ratio=decrease:force_divisible_by=2,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2"
        };
        var filter = sizing + ",setsar=1,format=yuv420p";
        filter += clip.Look switch
        {
            ColorLook.Warm => ",colorbalance=rs=0.06:bs=-0.05,eq=saturation=1.08",
            ColorLook.Cool => ",colorbalance=rs=-0.04:bs=0.07,eq=saturation=0.92",
            ColorLook.Cinema => ",colorbalance=rs=-0.04:gs=0.02:bs=0.05:rh=0.06:bh=-0.04,eq=contrast=1.12:saturation=0.85:brightness=-0.015",
            ColorLook.BlackAndWhite => ",hue=s=0",
            _ => ""
        };
        if (clip.Brightness != 0 || clip.Contrast != 1 || clip.Saturation != 1)
            filter += $",eq=brightness={F(clip.Brightness)}:contrast={F(clip.Contrast)}:saturation={F(clip.Saturation)}";
        if (clip.Warmth != 0) filter += $",colorbalance=rs={F(clip.Warmth * .15)}:bs={F(-clip.Warmth * .15)}";
        return filter;
    }

    public static async Task RenderClipFrameAsync(TimelineClip clip, string output, double time, CancellationToken token)
    {
        var asset = clip.Kind == MediaKind.Color ? new MediaAsset() : await ProbeAsync(clip.SourcePath, token);
        var args = VisualInput(clip, 854, 480, Math.Clamp(time, clip.In, Math.Max(clip.In, clip.Out - .04)));
        args.AddRange(["-vf", AppearanceFilter(clip, asset, 854, 480), "-frames:v", "1", "-update", "1", output]);
        var result = await RunAsync("ffmpeg", args, token);
        if (result.ExitCode != 0) throw new InvalidOperationException("Could not prepare frame: " + result.Error);
    }

    public static async Task ExtractWaveAsync(TimelineClip clip, string output, CancellationToken token)
    {
        if (clip.Kind != MediaKind.Video || !(await ProbeAsync(clip.SourcePath, token)).HasAudio)
            throw new InvalidOperationException("Choose a video clip with sound to extract.");
        if (string.Equals(Path.GetFullPath(clip.SourcePath), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a new filename to keep the original media.");
        var staged = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, $".WinMovie-{Guid.NewGuid():N}.wav");
        try
        {
            var speed = clip.KeepPitch ? TempoFilters(clip.Speed) : $"asetrate={F(48000 * clip.Speed)},aresample=48000";
            var result = await RunAsync("ffmpeg", ["-hide_banner", "-loglevel", "error", "-y", "-ss", F(clip.In), "-t", F(clip.Out - clip.In),
                "-i", clip.SourcePath, "-vn", "-af", $"aresample=48000,{speed},apad,atrim=duration={F(clip.Duration)}",
                "-c:a", "pcm_s24le", "-ar", "48000", "-ac", "2", staged], token);
            if (result.ExitCode != 0) throw new InvalidOperationException("Could not extract audio: " + result.Error);
            token.ThrowIfCancellationRequested();
            File.Move(staged, output, true);
        }
        finally { try { File.Delete(staged); } catch { } }
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
        IEnumerable<string> arguments, CancellationToken token = default, Action<string>? onOutputLine = null)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        Process process;
        try { process = Process.Start(start) ?? throw new InvalidOperationException($"Could not launch {executable}."); }
        catch (System.ComponentModel.Win32Exception ex)
        { throw new InvalidOperationException($"Could not find or start {executable}. Install FFmpeg and ffprobe and add them to PATH.", ex); }
        using (process)
        {
            using var registration = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
            async Task<string> ReadOutputAsync()
            {
                var text = new StringBuilder();
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                {
                    if (text.Length < 2_000_000) text.AppendLine(line);
                    onOutputLine?.Invoke(line);
                }
                return text.ToString();
            }
            var output = ReadOutputAsync();
            var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(token); }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(output, error);
                throw;
            }
            token.ThrowIfCancellationRequested();
            return (process.ExitCode, await output, await error);
        }
    }
}
