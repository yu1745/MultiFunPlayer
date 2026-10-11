using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Channels;

namespace MultiFunPlayer.OutputTarget.TCodeBuffered;

// One receiver lives for the entire serial connection. ACK timeouts retry the
// exact frame without discarding partial input or stopping the receiver.
internal sealed class TCodeBufferedClient : IAsyncDisposable
{
    private readonly Func<string, CancellationToken, Task> _write;
    private readonly CancellationTokenSource _receiveCancellation = new();
    private readonly Channel<ulong[]> _replies = Channel.CreateBounded<ulong[]>(new BoundedChannelOptions(64)
    {
        SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest
    });
    private readonly Task _receiver;
    private readonly TimeSpan _ackTimeout;
    private ulong _request;
    private ulong _query = 4294967296;
    private double _offset;
    private ulong? _beginSession;

    public ulong Boot { get; private set; }
    public ulong Session { get; private set; }
    public ulong[] Status { get; private set; }
    public TCodeBufferedLimits Limits { get; private set; }
    public double UncertaintyMilliseconds { get; private set; }
    public bool MayOwnMotion => _beginSession.HasValue;
    public static double HostMicroseconds => Stopwatch.GetTimestamp() * (1000000d / Stopwatch.Frequency);
    public ulong Now => checked((ulong)Math.Max(0, Math.Round(HostMicroseconds + _offset)));

    public TCodeBufferedClient(Func<string, CancellationToken, Task> write,
        Func<CancellationToken, Task<string>> read, TimeSpan? ackTimeout = null)
    {
        _write = write;
        _ackTimeout = ackTimeout ?? TimeSpan.FromMilliseconds(700);
        _receiver = ReceiveAsync(read, _receiveCancellation.Token);
    }

    private async Task ReceiveAsync(Func<CancellationToken, Task<string>> read, CancellationToken token)
    {
        var buffer = new StringBuilder();
        try
        {
            while (!token.IsCancellationRequested)
            {
                var chunk = await read(token).ConfigureAwait(false);
                if (chunk == null) throw new IOException("TCode connection closed.");
                foreach (var c in chunk)
                {
                    if (c != '\n')
                    {
                        buffer.Append(c);
                        if (buffer.Length > 4096)
                            throw new InvalidDataException("TCode reply line exceeds the receive buffer.");
                        continue;
                    }
                    var line = buffer.ToString();
                    buffer.Clear();
                    var start = line.IndexOf("!4,", StringComparison.Ordinal);
                    if (start < 0) continue; // Boot logs and shell prompts are not ACKs.
                    if (!TCodeBufferedProtocol.TryDecode(line[start..], out var fields)
                        || !ValidReply(fields))
                        throw new InvalidDataException("Invalid TCode 0.4 acknowledgement (format or CRC32).");
                    _replies.Writer.TryWrite(fields);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e) { _replies.Writer.TryComplete(e); }
        finally { _replies.Writer.TryComplete(); }
    }

    private static bool ValidReply(ulong[] fields)
        => fields.Length >= 6 && fields[5] <= 5 &&
            ((fields[0] == 100 && fields.Length == 22 && fields[8] == 512
                && fields.Skip(9).Take(6).All(x => x <= 512))
            || (fields[0] == 101 && fields.Length == 20));

    public async Task<TCodeBufferedLimits> QueryLimitsAsync(CancellationToken token)
    {
        var reply = await TransactAsync(6, token).ConfigureAwait(false);
        if (reply[2] != Boot || reply[3] != Session)
            throw new IOException("P4 identity changed while querying motion limits.");
        if (reply[0] != 101)
            throw new IOException("P4 does not support motion-limit negotiation. Update the firmware before buffered playback.");
        return Limits = TCodeBufferedLimits.Decode(reply);
    }

    public async Task<ulong[]> SendAsync(ulong operation, CancellationToken token, params ulong[] args)
    {
        var reply = await TransactAsync(operation, token, args).ConfigureAwait(false);
        if (reply[0] != 100)
            throw new InvalidDataException("Expected a P4 status acknowledgement.");
        if (Status != null && reply[2] != Boot)
            throw new IOException("P4 restarted; buffered playback stopped.");
        if (Status != null && operation != 1 && reply[3] != Session)
            throw new IOException("P4 session changed; buffered playback stopped.");
        if (reply[4] != 0)
            throw new TCodeBufferedException(reply[4], operation, reply[5]);
        if (Status != null && operation == 1 && reply[3] != Session + 1)
            throw new IOException("Unexpected TCode session after BEGIN.");
        Boot = reply[2];
        Session = reply[3];
        Status = reply;
        return reply;
    }

    private async Task<ulong[]> TransactAsync(ulong operation, CancellationToken token, params ulong[] args)
    {
        var id = operation is 0 or 6 ? ++_query : ++_request;
        var wire = TCodeBufferedProtocol.Encode(new ulong[] { operation, id, Boot, Session }.Concat(args));
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(_ackTimeout);
            try
            {
                await _write(wire, timeout.Token).ConfigureAwait(false);
                while (true)
                {
                    var reply = await _replies.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
                    if (reply[1] == id) return reply;
                }
            }
            catch (ChannelClosedException e)
            {
                throw new IOException("TCode receive failed: " + (e.InnerException?.Message ?? e.Message), e);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                if (attempt == 2)
                    throw new IOException("TCode 0.4 ACK timeout after three identical transmissions.");
            }
        }
        throw new UnreachableException();
    }

    public async Task SynchronizeAsync(CancellationToken token)
    {
        var best = double.PositiveInfinity;
        for (var i = 0; i < 5; i++)
        {
            var before = HostMicroseconds;
            var reply = await SendAsync(0, token).ConfigureAwait(false);
            var after = HostMicroseconds;
            if (after - before < best)
            {
                best = after - before;
                _offset = reply[6] - (before + after) / 2;
            }
        }
        UncertaintyMilliseconds = best / 2000;
        if (best > 200000)
            throw new IOException("P4 clock round trip exceeds 200 ms; buffered playback was not armed.");
    }

    public async Task BeginAsync(ulong mask, ulong start, CancellationToken token)
    {
        if (Limits == null)
            throw new InvalidOperationException("Query device motion limits before BEGIN.");
        // Mark BEFORE sending: a lost BEGIN ACK may still mean the device has
        // accepted a new session. Cleanup queries that session before STOP.
        _beginSession = Session;
        try { await SendAsync(1, token, mask, start, Limits.Revision, 1).ConfigureAwait(false); }
        catch (TCodeBufferedException) { _beginSession = null; throw; }
    }

    public async Task StopAsync(CancellationToken token)
    {
        if (!_beginSession.HasValue) return;
        if (Session == _beginSession.Value)
        {
            // Only needed if BEGIN's ACK was lost. For an acknowledged session
            // send STOP immediately, even if the receive pump has failed.
            var reply = await TransactAsync(0, token).ConfigureAwait(false);
            if (reply[2] != Boot || (reply[3] != _beginSession.Value && reply[3] != _beginSession.Value + 1))
                throw new IOException("P4 identity changed; cannot acknowledge STOP for the owned session.");
            Session = reply[3];
        }
        await SendAsync(5, token).ConfigureAwait(false);
        _beginSession = null;
    }

    public async ValueTask DisposeAsync()
    {
        _receiveCancellation.Cancel();
        try { await _receiver.ConfigureAwait(false); }
        finally { _receiveCancellation.Dispose(); }
    }
}
