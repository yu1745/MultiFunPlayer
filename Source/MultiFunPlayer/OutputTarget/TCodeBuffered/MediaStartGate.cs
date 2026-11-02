using MultiFunPlayer.Common;
namespace MultiFunPlayer.OutputTarget.TCodeBuffered;

// Raw source reports only: UI interpolation and a play flag cannot arm motion.
internal sealed class MediaStartGate(long afterSequence, MediaClockSample baseline = null)
{
    private MediaClockSample previous = baseline;
    private int advancing;
    public bool Observe(BufferedPlaybackSnapshot current, double hostNow)
    {
        var sample = current.MediaSample;
        if (!current.IsPlaying || sample == null ||
            !double.IsFinite(sample.Position) || !double.IsFinite(current.Speed) || current.Speed <= 0 ||
            hostNow < sample.HostMicroseconds || hostNow - sample.HostMicroseconds > 1500000)
        { previous = null; advancing = 0; return false; }
        if (sample.Sequence <= afterSequence || previous?.Sequence == sample.Sequence) return false;
        if (previous != null)
        {
            var dt = (sample.HostMicroseconds - previous.HostMicroseconds) / 1e6;
            var dp = sample.Position - previous.Position;
            advancing = dt > 0 && dp > .001 && Math.Abs(dp - dt * current.Speed) <= .100
                ? advancing + 1 : 0;
        }
        previous = sample;
        return advancing >= 1;
    }
    public static double PositionNow(BufferedPlaybackSnapshot snapshot, double hostNow)
    {
        var sample = snapshot.MediaSample;
        if (sample == null || !double.IsFinite(sample.Position) || hostNow < sample.HostMicroseconds ||
            hostNow - sample.HostMicroseconds > 1500000)
            throw new MediaSynchronizationException("Waiting for fresh media position reports.");
        return sample.Position + (snapshot.IsPlaying ? (hostNow - sample.HostMicroseconds) / 1e6 * snapshot.Speed : 0);
    }
}
internal sealed class MediaSynchronizationException(string message) : System.IO.IOException(message);
