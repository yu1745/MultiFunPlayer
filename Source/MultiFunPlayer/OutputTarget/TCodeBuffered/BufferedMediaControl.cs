using MultiFunPlayer.Common;
using Stylet;

namespace MultiFunPlayer.OutputTarget.TCodeBuffered;

// Observe user requests even while media is paused by the preloader. A second
// Pause must cancel preparation, not get overwritten by our scheduled Play.
internal sealed class BufferedMediaControl : IDisposable, IHandle<MediaPlayPauseMessage>,
    IHandle<MediaSeekMessage>, IHandle<MediaChangeSpeedMessage>, IHandle<MediaChangePathMessage>
{
    private readonly IEventAggregator _events;
    private MediaPlayPauseMessage _ownMessage;
    private long _revision;
    private int _playIntent = -1;
    public long Revision => Interlocked.Read(ref _revision);
    public bool WantsPlayback => Volatile.Read(ref _playIntent) != 0;

    public BufferedMediaControl(IEventAggregator events)
    {
        _events = events;
        events.Subscribe(this);
    }

    public void SetPlaying(bool playing)
    {
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
    }
    public void Handle(MediaSeekMessage message) => Interlocked.Increment(ref _revision);
    public void Handle(MediaChangeSpeedMessage message) => Interlocked.Increment(ref _revision);
    public void Handle(MediaChangePathMessage message) => Interlocked.Increment(ref _revision);
    public void Dispose() => _events.Unsubscribe(this);
}
