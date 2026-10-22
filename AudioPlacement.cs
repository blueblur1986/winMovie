namespace WinMovie;

public sealed record AudioPlacement(double Start, double In, double ReadDuration, double Duration, double Speed, double LeadingSilence)
{
    public static AudioPlacement Resolve(MovieProject project, MusicClip audio)
    {
        var index = project.Clips.FindIndex(x => x.Id == audio.LinkedClipId);
        if (index < 0) return new(audio.Start, audio.In, audio.Duration, audio.Duration, 1, 0);
        var clip = project.Clips[index];
        var speed = clip.Speed / audio.LinkedSourceSpeed;
        var sourceIn = audio.In + (clip.In - audio.LinkedSourceIn) / audio.LinkedSourceSpeed - audio.SyncOffset * speed;
        var leading = Math.Max(0, audio.In - sourceIn) / speed;
        var readIn = Math.Clamp(sourceIn, audio.In, audio.Out);
        var read = Math.Max(0, Math.Min(audio.Out - readIn, (clip.Duration - leading) * speed));
        return new(new TimelineLayout(project.Clips).Starts[index], readIn, read, clip.Duration, speed, leading);
    }
}
