namespace MultiFunPlayer.Common;

// Positions/offsets are seconds; values are normalized. Keyframes are published
// by ScriptViewModel as a new collection when a script is replaced.
internal sealed record BufferedScriptAxis(string Name, IReadOnlyList<Keyframe> Keyframes,
    double Offset, double Scale, bool Invert, double DefaultValue,
    double Minimum = 0, double Maximum = 1, string UnsupportedReason = null, IReadOnlyList<Keyframe> OriginalKeyframes = null);

internal sealed record BufferedPlaybackSnapshot(bool IsPlaying, double Position, double Speed,
    long Revision, IReadOnlyList<BufferedScriptAxis> Axes, long FilterRevision = 0, MediaClockSample MediaSample = null)
{
    public bool HasSameMotionTimeline(BufferedPlaybackSnapshot other)
        => HasSameTimeline(other with { Axes = other.Axes.Select(a => {
            var original=Axes.FirstOrDefault(b=>b.Name==a.Name);
            return original==null?a:a with {Minimum=original.Minimum,Maximum=original.Maximum};
        }).ToArray() });
    public bool HasSameTimeline(BufferedPlaybackSnapshot other)
        => Revision == other.Revision && Speed == other.Speed && Axes.Count == other.Axes.Count
        && Axes.Zip(other.Axes).All(pair =>
            ReferenceEquals(pair.First.OriginalKeyframes ?? pair.First.Keyframes, pair.Second.OriginalKeyframes ?? pair.Second.Keyframes)
            && pair.First with { Keyframes = null, OriginalKeyframes = null } == pair.Second with { Keyframes = null, OriginalKeyframes = null });
}

internal sealed record MediaClockSample(double Position, double HostMicroseconds, long Sequence);
