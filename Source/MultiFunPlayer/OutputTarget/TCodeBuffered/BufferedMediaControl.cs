using MultiFunPlayer.Common;
using Stylet;

namespace MultiFunPlayer.OutputTarget.TCodeBuffered;

// Observe user requests even while media is paused by the preloader. A second
// Pause must cancel preparation, not get overwritten by our scheduled Play.
internal sealed class BufferedMediaControl : IDisposable, IHandle<MediaPlayPauseMessage>,
    IHandle<MediaSeekMessage>, IHandle<MediaChangeSpeedMessage>, IHandle<MediaChangePathMessage>
{
    private readonly IEventAggregator _events;
    private readonly Action<string> _diagnostic;
    private MediaPlayPauseMessage _ownMessage;
    private long _revision;
    private int _playIntent = -1;
    public long Revision => Interlocked.Read(ref _revision);
    public bool WantsPlayback => Volatile.Read(ref _playIntent) != 0;

    public BufferedMediaControl(IEventAggregator events, Action<string> diagnostic = null)
    {
        _events = events;
        _diagnostic = diagnostic;
        events.Subscribe(this);
    }

    public void SetPlaying(bool playing)
    {
        _diagnostic?.Invoke($"MEDIA auto request playing={playing} controlRevision={Revision}");
        var message = new MediaPlayPauseMessage(playing);
        _ownMessage = message;
        try { _events.Publish(message); }
        finally { _ownMessage = null; }
    }

    public void Handle(MediaPlayPauseMessage message)
    {
        if (ReferenceEquals(message, _ownMessage)) return;
        Volatile.Write(ref _playIntent, message.ShouldBePlaying ? 1 : 0);
        Interlocked.Increment(ref _revision);
        _diagnostic?.Invoke($"MEDIA external request playing={message.ShouldBePlaying} controlRevision={Revision}");
    }
    public void Handle(MediaSeekMessage message) { Interlocked.Increment(ref _revision); _diagnostic?.Invoke($"MEDIA seek controlRevision={Revision}"); }
    public void Handle(MediaChangeSpeedMessage message) { Interlocked.Increment(ref _revision); _diagnostic?.Invoke($"MEDIA speed change controlRevision={Revision}"); }
    public void Handle(MediaChangePathMessage message) { Interlocked.Increment(ref _revision); _diagnostic?.Invoke($"MEDIA path change controlRevision={Revision}"); }
    public void Dispose() => _events.Unsubscribe(this);
}
