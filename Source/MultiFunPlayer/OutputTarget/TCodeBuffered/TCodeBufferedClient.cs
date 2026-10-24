using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Channels;
using System.Collections.Concurrent;
using MultiFunPlayer.OutputTarget.Tracking;

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
    private readonly Action<string> _diagnostic;
    private readonly TrackingMonitor _tracking;
    private readonly Func<bool> _trackingEnabled;
    private readonly Func<int> _trackingPeriodMs;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Channel<ulong[]> _trackingReplies = Channel.CreateBounded<ulong[]>(4);
    private readonly CancellationTokenSource _trackingCancellation = new();
    private Task _trackingPump;
    private ulong _trackingRequest = 1UL << 40;
    private bool _trackingAttempted;
    private ulong _request;
    private ulong _query = 4294967296;
    private double _offset;
    private ulong? _beginSession;
    private readonly ConcurrentQueue<TCodeDeviceDiagnostic> _deviceDiagnostics = new();
    private string _commandDiagnostic;
    public bool DeviceDiagnosticsEnabled { get; private set; }

    public ulong Boot { get; private set; }
    public ulong Session { get; private set; }
    public ulong[] Status { get; private set; }
    public double UncertaintyMilliseconds { get; private set; }
    public bool MayOwnMotion => _beginSession.HasValue;
    public static double HostMicroseconds => Stopwatch.GetTimestamp() * (1000000d / Stopwatch.Frequency);
    public ulong Now => checked((ulong)Math.Max(0, Math.Round(HostMicroseconds + _offset)));

    public TCodeBufferedClient(Func<string, CancellationToken, Task> write,
        Func<CancellationToken, Task<string>> read, TimeSpan? ackTimeout = null, Action<string> diagnostic = null,
        TrackingMonitor tracking = null, Func<bool> trackingEnabled = null, Func<int> trackingPeriodMs = null)
    {
        _write = write;
        _ackTimeout = ackTimeout ?? TimeSpan.FromMilliseconds(700);
        _diagnostic = diagnostic;
        _tracking = tracking;
        _trackingEnabled = trackingEnabled ?? (() => true);
        _trackingPeriodMs = trackingPeriodMs ?? (() => 20);
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
                    var telemetry = line.IndexOf(TrackingPacket.Prefix, StringComparison.Ordinal);
                    if (telemetry >= 0)
                    {
                        // Telemetry corruption is a missing measurement, never
                        // an ACK failure and never a reason to halt playback.
                        if (TrackingPacket.TryDecode(line[telemetry..], out var packet, out var error))
                        {
                            if (packet.Boot == Boot && Boot != 0) _tracking?.Accept(packet, Now, UncertaintyMilliseconds);
                        }
                        else _tracking?.BadPacket(error);
                        continue;
                    }
                    var start = line.IndexOf("!4,", StringComparison.Ordinal);
                    if (start < 0)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            var deviceError = line.Contains("ERR ", StringComparison.Ordinal)
                                || System.Text.RegularExpressions.Regex.IsMatch(line, @"\bE \(\d+\)");
                            _diagnostic?.Invoke(deviceError ? $"DEVICE ERROR text: {line.Trim()}" : $"RX non-ACK: {line.Trim()}");
                        }
                        continue;
                    }
                    if (!TCodeBufferedProtocol.TryDecode(line[start..], out var fields))
                        throw new InvalidDataException("Invalid TCode 0.4 acknowledgement (format or CRC32).");
                    if (fields.Length >= 6 && fields[1] >= (1UL << 40) && fields[0] != 105)
                    {
                        // Independent read-only subscription IDs: these replies
                        // must never consume a playback transaction's ACK.
                        _trackingReplies.Writer.TryWrite(fields);
                        continue;
                    }
                    if (fields[0] == 105)
                    {
                        if (fields.Length != 18 || fields[6] > 7 || fields[8] is > 5 and not 255
                            || fields[9] > 7 || fields.Skip(12).Any(x => x > uint.MaxValue))
                            throw new InvalidDataException("Invalid TCode 0.4 diagnostic event.");
                        var entry = TCodeDeviceDiagnostic.Decode(fields);
                        _deviceDiagnostics.Enqueue(entry);
                        while (_deviceDiagnostics.Count > 64) _deviceDiagnostics.TryDequeue(out _);
                        _diagnostic?.Invoke(entry.LogLine);
                        continue; // An unsolicited event is never an ACK.
                    }
                    if (!ValidReply(fields))
                        throw new InvalidDataException("Invalid TCode 0.4 acknowledgement schema.");
                    _replies.Writer.TryWrite(fields);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e) { _replies.Writer.TryComplete(e); }
        finally { _replies.Writer.TryComplete(); }
    }

    private static bool ValidReply(ulong[] fields)
        => fields.Length >= 6 && fields[5] <= 7 &&
            ((fields[0] == 100 && fields.Length == 22 && fields[8] == 512
                && fields.Skip(9).Take(6).All(x => x <= 512))
            || (fields[0] == 101 && fields.Length == 13)
            || (fields[0] == 102 && fields.Length == 11)
            || (fields[0] == 103 && fields.Length == 7 && fields[6] <= 63)
            || (fields[0] == 104 && fields.Length == 10 && fields[6] == 1 && fields[9] <= 1));

    public async Task EnableDeviceDiagnosticsAsync(CancellationToken token)
    {
        var reply = await TransactAsync(11, token, 1).ConfigureAwait(false);
        if (reply[2] != Boot || reply[3] != Session)
            throw new IOException("P4 identity changed while enabling diagnostics.");
        DeviceDiagnosticsEnabled = reply[0] == 104 && reply[4] == 0 && reply[9] == 1;
        _diagnostic?.Invoke(DeviceDiagnosticsEnabled
            ? $"DEVICE diagnostics enabled version=1 capacity={reply[7]} dropped={reply[8]}"
            : "DEVICE diagnostics unavailable: update P4 firmware for detailed error reasons.");
    }

    public IOException DeviceStateException(string context)
    {
        var entries = _deviceDiagnostics.Where(d => d.Boot == Boot && d.Session == Session && !d.IsContext).ToArray();
        var fault = entries.LastOrDefault(d => d.State == 5);
        var relevant = fault ?? entries.LastOrDefault();
        return new IOException(context + $" state={Status?[5]}; underruns={Status?.ElementAtOrDefault(21)}." +
            (relevant == null ? " No detailed device diagnostic received." : $" P4 event={relevant.Sequence}: {relevant.Message}"));
    }

    private TCodeBufferedException Rejected(ulong[] reply, ulong operation)
        => new(reply[4], operation, reply[5], _commandDiagnostic);

    public async Task<ulong> QuerySupportedAxesAsync(CancellationToken token)
    {
        var reply = await TransactAsync(10, token).ConfigureAwait(false);
        if (reply[2] != Boot || reply[3] != Session)
            throw new IOException("P4 identity changed while querying supported axes.");
        if (reply[0] != 103)
            throw new IOException("P4 firmware does not report supported axes. Update the P4 firmware.");
        if (reply[4] != 0) throw Rejected(reply, 10);
        return reply[6];
    }

    public Task<ulong[]> QueryTimingAsync(CancellationToken token) => TimingAsync(9, token);
    public Task<ulong[]> PauseAsync(ulong cursor, CancellationToken token) => TimingAsync(7, token, cursor);
    public Task<ulong[]> ResumeAsync(ulong start, CancellationToken token) => TimingAsync(8, token, start);
    public Task<ulong[]> ReleaseMediaAsync(CancellationToken token) => TimingAsync(14, token);

    public async Task ArmMediaAsync(ulong epoch, CancellationToken token)
    {
        try { await SendAsync(13, token, epoch).ConfigureAwait(false); }
        catch (TCodeBufferedException e) when (e.Code == 5)
        { throw new IOException("P4 does not support the media-start gate. Update the P4 firmware together with MFP.", e); }
    }

    public async Task RefreshPausedStatusAsync(CancellationToken token)
    {
        var before = HostMicroseconds;
        var reply = await SendAsync(0, token).ConfigureAwait(false);
        var after = HostMicroseconds;
        if (reply[5] is 6 or 7 && after - before <= 200000)
        {
            // A long user pause must not accumulate host/device clock drift.
            // Only the clock mapping changes; accepted device timestamps do not.
            _offset = reply[6] - (before + after) / 2;
            UncertaintyMilliseconds = (after - before) / 2000;
        }
    }

    private async Task<ulong[]> TimingAsync(ulong operation, CancellationToken token, params ulong[] args)
    {
        var reply = await TransactAsync(operation, token, args).ConfigureAwait(false);
        if (reply[0] != 102)
            throw new IOException("P4 firmware does not support retained-queue pause/resume. Update the P4 firmware.");
        if (reply[2] != Boot || reply[3] != Session)
            throw new IOException("P4 identity changed during pause/resume.");
        if (reply[4] != 0) throw Rejected(reply, operation);
        if (Status != null)
        {
            Status = (ulong[])Status.Clone();
            Status[5] = reply[5]; Status[6] = reply[6];
        }
        return reply;
    }

    public async Task<BufferedDevicePose> QueryPoseAsync(CancellationToken token)
    {
        var reply = await TransactAsync(6, token).ConfigureAwait(false);
        if (reply[2] != Boot || reply[3] != Session)
            throw new IOException("P4 identity changed while querying its pose.");
        if (reply[4] != 0)
            throw Rejected(reply, 6);
        if (reply[0] != 101)
            throw new IOException("Expected a P4 pose response.");
        return BufferedDevicePose.Decode(reply);
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
            throw Rejected(reply, operation);
        if (Status != null && operation == 1 && reply[3] != Session + 1)
            throw new IOException("Unexpected TCode session after BEGIN.");
        Boot = reply[2];
        Session = reply[3];
        Status = reply;
        return reply;
    }

    private Task<ulong[]> TransactAsync(ulong operation, CancellationToken token, params ulong[] args)
        => TransactWithAttemptsAsync(operation, token, 3, args);

    private async Task<ulong[]> TransactWithAttemptsAsync(ulong operation, CancellationToken token, int attempts, params ulong[] args)
    {
        var id = operation is 0 or 6 or 9 or 10 or 11 ? ++_query : ++_request;
        var before = _deviceDiagnostics.LastOrDefault()?.Sequence ?? 0;
        _commandDiagnostic = null;
        var wire = TCodeBufferedProtocol.Encode(new ulong[] { operation, id, Boot, Session }.Concat(args));
        var transactionStarted = HostMicroseconds;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(_ackTimeout);
            try
            {
                var sent = HostMicroseconds;
                _diagnostic?.Invoke($"TX op={operation} id={id} boot={Boot} session={Session} attempt={attempt + 1}/{attempts} args=[{string.Join(',', args)}]");
                await WriteWireAsync(wire, timeout.Token).ConfigureAwait(false);
                while (true)
                {
                    var reply = await _replies.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
                    if (reply[1] == id)
                    {
                        _commandDiagnostic = string.Join("; ", _deviceDiagnostics.Where(d => d.Sequence > before
                            && d.Boot == reply[2] && d.Session == reply[3] && !d.IsContext
                            && (d.Request == id || d.Reason == 26)).Select(d => d.Message));
                        _diagnostic?.Invoke(FormattableString.Invariant($"ACK op={operation} id={id} rttMs={(HostMicroseconds - sent) / 1000:F2} totalMs={(HostMicroseconds - transactionStarted) / 1000:F2} fields=[{string.Join(',', reply)}]"));
                        return reply;
                    }
                    _diagnostic?.Invoke($"ACK ignored expectedId={id} receivedId={reply[1]}");
                }
            }
            catch (ChannelClosedException e)
            {
                throw new IOException("TCode receive failed: " + (e.InnerException?.Message ?? e.Message), e);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                _diagnostic?.Invoke($"ACK TIMEOUT op={operation} id={id} attempt={attempt + 1}/{attempts}");
                if (attempt == attempts - 1)
                    throw new IOException($"TCode 0.4 ACK timeout after {attempts} identical transmissions.");
            }
        }
        throw new UnreachableException();
    }

    public async Task SynchronizeAsync(CancellationToken token)
    {
        var syncStarted = HostMicroseconds;
        // Opening a serial port can reset the P4. Allow its boot to finish before
        // collecting clock samples; a boot-delayed ACK is not a timing sample.
        if (Status == null)
            await TransactWithAttemptsAsync(0, token, 15).ConfigureAwait(false);
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
        _diagnostic?.Invoke(FormattableString.Invariant($"SYNC elapsedMs={(HostMicroseconds - syncStarted) / 1000:F2} uncertaintyMs={UncertaintyMilliseconds:F2} offsetUs={_offset:F0}"));
        if (best > 200000)
            throw new IOException("P4 clock round trip exceeds 200 ms; buffered playback was not armed.");
    }

    public async Task BeginAsync(ulong mask, ulong start, BufferedSessionLimits limits, CancellationToken token)
    {
        var args = new ulong[] { mask, start, 1 }.Concat(limits.Encode()).ToArray();
        // Mark BEFORE sending: a lost BEGIN ACK may still mean the device has
        // accepted a new session. Cleanup queries that session before STOP.
        _beginSession = Session;
        try { await SendAsync(1, token, args).ConfigureAwait(false); }
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

    private async Task WriteWireAsync(string wire, CancellationToken token)
    {
        await _writeGate.WaitAsync(token).ConfigureAwait(false);
        try { await _write(wire, token).ConfigureAwait(false); }
        finally { _writeGate.Release(); }
    }

    // Monitoring has its own reply channel and bounded lease. Its subscription
    // ACKs/timeouts never hold the playback transaction reader or refill loop.
    public void StartTracking()
    {
        if (_tracking != null && _trackingPump == null)
            _trackingPump = TrackingPumpAsync(_trackingCancellation.Token);
    }

    private async Task TrackingPumpAsync(CancellationToken token)
    {
        var active = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var enabled = _trackingEnabled();
                if (!enabled && !active)
                {
                    _tracking.SetAvailability("跟随监测已关闭");
                    await Task.Delay(250, token).ConfigureAwait(false);
                    continue;
                }
                var request = ++_trackingRequest;
                var period = enabled ? (ulong)Math.Clamp(_trackingPeriodMs(), 20, 200) : 0UL;
                var lease = enabled ? 1500UL : 0UL;
                var wire = TCodeBufferedProtocol.Encode([12, request, Boot, Session, period, lease]);
                _trackingAttempted = true;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(300);
                try
                {
                    await WriteWireAsync(wire, timeout.Token).ConfigureAwait(false);
                    ulong[] reply;
                    do { reply = await _trackingReplies.Reader.ReadAsync(timeout.Token).ConfigureAwait(false); } while (reply[1] != request);
                    if (reply[0] != 106)
                    {
                        _tracking.SetAvailability("固件不支持实时跟随回传，请更新 P4 固件");
                        _diagnostic?.Invoke("TRACKING unsupported; playback continues without telemetry");
                        return;
                    }
                    if (reply.Length != 10 || reply[2] != Boot || reply[4] != 0 || reply[6] != 1 ||
                        reply[7] != period || reply[8] != lease || reply[9] != TrackingPacket.Model)
                    {
                        _tracking.SetAvailability("跟随协议／模型不匹配或设备已重启");
                        return;
                    }
                    active = enabled;
                    if (!active) _tracking.SetAvailability("跟随监测已关闭");
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { _tracking.SetAvailability("跟随订阅暂未应答，正在重试；动作输出不受此超时控制"); }
                await Task.Delay(500, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { _tracking.SetAvailability("跟随回传不可用：" + error.Message); }
    }

    public async ValueTask DisposeAsync()
    {
        _trackingCancellation.Cancel();
        if (_trackingPump != null) await _trackingPump.ConfigureAwait(false);
        if (_trackingAttempted)
        {
            using var cleanup = new CancellationTokenSource(100);
            try { await WriteWireAsync(TCodeBufferedProtocol.Encode([12, ++_trackingRequest, Boot, Session, 0, 0]), cleanup.Token).ConfigureAwait(false); }
            catch { /* The 1.5 s device-side lease is the disconnect fallback. */ }
        }
        _receiveCancellation.Cancel();
        try { await _receiver.ConfigureAwait(false); }
        finally { _receiveCancellation.Dispose(); _trackingCancellation.Dispose(); _writeGate.Dispose(); }
    }
}
