using System.Text.Json.Serialization;

namespace WinMovie;

public sealed class MediaAsset
{
    public string Path { get; set; } = "";
    public double Duration { get; set; }
    public bool HasAudio { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    [JsonIgnore] public string? ThumbnailPath { get; set; }
    [JsonIgnore] public List<string> FilmstripFrames { get; set; } = [];
    [JsonIgnore] public string Name => System.IO.Path.GetFileName(Path);
}

public enum TransitionKind { Cut, Crossfade, FadeThroughBlack, FadeThroughWhite, WipeLeft, WipeRight, SlideLeft, SlideRight }
public enum TitlePreset { None, Center, LowerThird, Opening }

public sealed class TimelineClip
{
    public string SourcePath { get; set; } = "";
    public double In { get; set; }
    public double Out { get; set; }
    public double Speed { get; set; } = 1;
    public bool KeepPitch { get; set; } = true;
    public double Volume { get; set; } = 1;
    public bool Normalize { get; set; }
    public TransitionKind TransitionIn { get; set; }
    public double TransitionDuration { get; set; } = .5;
    public TitlePreset TitlePreset { get; set; }
    public string Title { get; set; } = "";
    public bool FadeIn { get; set; }
    public bool FadeOut { get; set; }
    public double FadeInDuration { get; set; } = .35;
    public double FadeOutDuration { get; set; } = .35;
    public double Duration => Math.Max(0, Out - In) / Math.Max(.01, Speed);
}

public sealed class TitleItem
{
    public double Start { get; set; }
    public double Duration { get; set; } = 3;
    public string Text { get; set; } = "Your title";
    public TitlePreset Preset { get; set; } = TitlePreset.Center;
    public string Font { get; set; } = "Segoe UI";
    public int FontSize { get; set; } = 62;
    public string Color { get; set; } = "FFFFFF";
    public string BackgroundColor { get; set; } = "000000";
    public double BackgroundOpacity { get; set; } = .45;
    public bool Shadow { get; set; } = true;
    public bool Outline { get; set; }
}

public sealed class MusicClip
{
    public string Path { get; set; } = "";
    public double Start { get; set; }
    public double In { get; set; }
    public double Out { get; set; }
    public double Volume { get; set; } = .45;
    public bool FadeOut { get; set; } = true;
    public double FadeOutDuration { get; set; } = 2;
    public double Duration => Math.Max(0, Out - In);
}

public sealed class MovieProject
{
    public List<MediaAsset> Assets { get; set; } = [];
    public List<TimelineClip> Clips { get; set; } = [];
    public List<TitleItem> Titles { get; set; } = [];
    public List<MusicClip> Music { get; set; } = [];
}
