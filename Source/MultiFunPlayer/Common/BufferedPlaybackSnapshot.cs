namespace MultiFunPlayer.Common;

// Positions/offsets are seconds; values are normalized. Keyframes are published
// by ScriptViewModel as a new collection when a script is replaced.
internal sealed record BufferedScriptAxis(string Name, IReadOnlyList<Keyframe> Keyframes,
    double Offset, double Scale, bool Invert, double DefaultValue,
    double Minimum = 0, double Maximum = 1, string UnsupportedReason = null);

internal sealed record BufferedPlaybackSnapshot(bool IsPlaying, double Position, double Speed,
    long Revision, IReadOnlyList<BufferedScriptAxis> Axes)
{
    public bool HasSameTimeline(BufferedPlaybackSnapshot other)
        => Revision == other.Revision && Speed == other.Speed && Axes.Count == other.Axes.Count
        && Axes.Zip(other.Axes).All(pair =>
            ReferenceEquals(pair.First.Keyframes, pair.Second.Keyframes)
            && pair.First with { Keyframes = null } == pair.Second with { Keyframes = null });
}
