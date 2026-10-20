using System.Text.Json;

namespace WinMovie;

public static class ProjectDocument
{
    public static string Snapshot(MovieProject project) => JsonSerializer.Serialize(project);
    public static MovieProject Clone(MovieProject project) => Parse(Snapshot(project));

    public static MovieProject Parse(string json)
    {
        var project = JsonSerializer.Deserialize<MovieProject>(json) ?? throw new InvalidDataException("The project file is empty.");
        Validate(project);
        return project;
    }

    public static void Validate(MovieProject project)
    {
        if (project.Assets is null || project.Clips is null || project.Titles is null || project.Music is null)
            throw new InvalidDataException("This project has a missing media or timeline list.");
        static bool Range(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
        static void Require(bool valid, string message) { if (!valid) throw new InvalidDataException(message); }
        foreach (var asset in project.Assets)
        {
            Require(asset is not null && !string.IsNullOrWhiteSpace(asset.Path), "A media file has no path.");
            Require(Range(asset!.Duration, .001, 604800) && asset.Width >= 0 && asset.Height >= 0, "A media file has invalid duration or dimensions.");
        }
        foreach (var clip in project.Clips)
        {
            Require(clip is not null && !string.IsNullOrWhiteSpace(clip.SourcePath), "A timeline clip has no source file.");
            Require(Range(clip!.In, 0, 604800) && Range(clip.Out, clip.In + .001, 604800) &&
                Range(clip.Speed, .25, 4) && Range(clip.Volume, 0, 4) &&
                Range(clip.TransitionDuration, .05, 10) && Enum.IsDefined(clip.TransitionIn) && Enum.IsDefined(clip.TitlePreset) &&
                Range(clip.FadeInDuration, .05, 10) && Range(clip.FadeOutDuration, .05, 10), "A timeline clip has invalid trim, speed, audio, or transition settings.");
        }
        foreach (var title in project.Titles)
        {
            Require(title is not null && title.Text is not null && title.Font is not null, "A title is incomplete.");
            Require(title!.Color is { Length: 6 } && title.Color.All(Uri.IsHexDigit) &&
                title.BackgroundColor is { Length: 6 } && title.BackgroundColor.All(Uri.IsHexDigit), "A title has an invalid text or background color.");
            Require(Range(title!.Start, 0, 604800) && Range(title.Duration, .05, 604800) &&
                Enum.IsDefined(title.Preset) && Range(title.FontSize, 16, 160) &&
                Range(title.BackgroundOpacity, 0, 1) && Range(title.ShadowDistance, 0, 30) && Range(title.ShadowBlur, 0, 30) &&
                Range(title.ShadowAngle, 0, 360) && Range(title.ShadowOpacity, 0, 1) && Range(title.OutlineThickness, 1, 16) &&
                Range(title.FadeInDuration, .05, 10) && Range(title.FadeOutDuration, .05, 10), "A title has invalid timing or style settings.");
        }
        foreach (var music in project.Music)
        {
            Require(music is not null && !string.IsNullOrWhiteSpace(music.Path), "A music track has no source file.");
            Require(Range(music!.Start, 0, 604800) && Range(music.In, 0, 604800) && Range(music.Out, music.In + .001, 604800) &&
                Range(music.Volume, 0, 4) && Range(music.FadeOutDuration, .05, 30), "A music track has invalid timing or audio settings.");
        }
    }

    public static void MigrateLegacyTitles(MovieProject project)
    {
        var layout = new TimelineLayout(project.Clips);
        for (var i = 0; i < project.Clips.Count; i++)
        {
            var clip = project.Clips[i];
            if (clip.TitlePreset == TitlePreset.None) continue;
            if (!string.IsNullOrWhiteSpace(clip.Title)) project.Titles.Add(new TitleItem
            {
                Start = layout.Starts[i], Duration = clip.Duration, Text = clip.Title, Preset = clip.TitlePreset,
                FontSize = clip.TitlePreset == TitlePreset.Opening ? 74 : clip.TitlePreset == TitlePreset.LowerThird ? 48 : 62
            });
            clip.TitlePreset = TitlePreset.None;
            clip.Title = "";
        }
    }
}
