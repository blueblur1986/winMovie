namespace WinMovie;

// The editor, preview, and exporter share one clock, including transition overlaps.
public sealed class TimelineLayout
{
    public double[] Starts { get; }
    public double[] Overlaps { get; }
    public double Duration { get; }

    public TimelineLayout(IReadOnlyList<TimelineClip> clips)
    {
        Starts = new double[clips.Count];
        Overlaps = new double[clips.Count];
        double end = 0;
        for (var i = 0; i < clips.Count; i++)
        {
            var overlap = i == 0 || clips[i].TransitionIn == TransitionKind.Cut ? 0 :
                Math.Min(Math.Clamp(clips[i].TransitionDuration, .05, 10), Math.Min(end, clips[i].Duration) / 2);
            Overlaps[i] = overlap;
            Starts[i] = end - overlap;
            end = Starts[i] + clips[i].Duration;
        }
        Duration = end;
    }

    public int ClipAt(IReadOnlyList<TimelineClip> clips, double position)
    {
        for (var i = clips.Count - 1; i >= 0; i--)
            if (position >= Starts[i]) return i;
        return clips.Count == 0 ? -1 : 0;
    }
}
