using System.Text.Json.Serialization;

namespace WinMovie;

public sealed class MediaAsset
{
    public string Path { get; set; } = "";
    public double Duration { get; set; }
    public bool HasAudio { get; set; }
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

public sealed class MovieProject
{
    public List<MediaAsset> Assets { get; set; } = [];
    public List<TimelineClip> Clips { get; set; } = [];
}
