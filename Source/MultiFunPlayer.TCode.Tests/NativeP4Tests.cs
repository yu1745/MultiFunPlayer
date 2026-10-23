using MultiFunPlayer.Common;
using MultiFunPlayer.OutputTarget.TCodeBuffered;
using MultiFunPlayer.OutputTarget.Tracking;
using System.Diagnostics;
using System.Globalization;

namespace MultiFunPlayer.TCode.Tests;

public sealed class NativeP4FactAttribute : FactAttribute
{
    public NativeP4FactAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("CTW_NATIVE_P4")))
            Skip = "Set CTW_NATIVE_P4 to the x86 P4 binary built by CTW test/p4_native.";
    }
}

public class NativeP4Tests
{
    [NativeP4Fact]
    public async Task StreamingTrackingDoesNotConsumePlaybackAcksOrStopTheSession()
    {
        using var firmware = new NativeP4 { FragmentWrites = false };
        using var monitor = new TrackingMonitor();
        await using var client = firmware.Client(tracking: monitor);
        var media = new Media([new(0, .5), new(1, .5), new(2, .55), new(4, .5)]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runner = new TCodeBufferedPlayback(client, media.Snapshot, media.SetPlaying,
            () => 0, () => true, _ => { });
        var run = runner.RunAsync(cancellation.Token);
        try
        {
            var until = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 3;
            while (Stopwatch.GetTimestamp() < until)
            {
                if (run.IsCompleted) await run;
                await Task.Delay(10);
            }
            Assert.True(monitor.Snapshot.Quality.Received >= 40);
            Assert.True(monitor.IsLive);
            Assert.InRange(monitor.Snapshot.Axes[0].Statistics.Rms.Value.Value, 0, .01);
            Assert.Contains(firmware.Requests, request => request[0] == 12);
            Assert.Contains(firmware.Requests, request => request[0] == 4);
        }
        finally { cancellation.Cancel(); await run; }
        Assert.Contains(firmware.Requests, request => request[0] == 5);
    }
    [NativeP4Fact]
    public async Task ProductionPauseFailureReachesMfpLogsAndExceptionBeforeStop()
    {
        using var firmware = new NativeP4(injectPauseStall: true) { FragmentWrites = false };
        var logs = new List<string>();
        await using var client = firmware.Client(diagnostic: logs.Add);
        var media = new Media([new(0, .5), new(1, .5), new(2, .55), new(4, .5)]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runner = new TCodeBufferedPlayback(client, media.Snapshot, media.SetPlaying,
            () => 0, () => true, _ => { });
        var run = runner.RunAsync(cancellation.Token);
        try
        {
            while (!media.Snapshot().IsPlaying || media.Snapshot().Position < 1.2)
            {
                if (run.IsCompleted) await run;
                await Task.Delay(5, cancellation.Token);
            }
            media.SetPlaying(false);
            var error = await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Contains("暂停收停计算失败", error.Message);
            Assert.Contains("control_dt_out_of_range", error.Message);
            Assert.Contains("underruns=0", error.Message);
            Assert.Contains(logs, line => line.StartsWith("DEVICE ERROR") && line.Contains("reason=22"));
            Assert.Equal(6, logs.Count(line => line.StartsWith("DEVICE CONTEXT") && line.Contains("reason=24")));
            Assert.Single(firmware.Requests.Where(f => f[0] == 7));
            Assert.Empty(firmware.Requests.Where(f => f[0] == 8));
            Assert.Equal(5UL, firmware.Requests[^1][0]);
        }
        finally
        {
            cancellation.Cancel();
            try { await run; } catch (IOException) { }
        }
    }

    [NativeP4Fact]
    public async Task PauseResumeRetainsSessionAndHasContinuousBoundedMotion()
    {
        using var firmware = new NativeP4 { FragmentWrites = false };
        await using var client = firmware.Client();
        var points = new List<Keyframe> { new(0, .5), new(1, .5) };
        for (var i = 1; i <= 24; i++) points.Add(new(1 + i * .25, i % 2 == 1 ? .8 : .2));
        var media = new Media(points.ToArray());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new TCodeBufferedPlayback(client, media.Snapshot, media.SetPlaying,
            () => 0, () => true, s =>
            {
                if (s.State == "Paused (queue retained)") paused.TrySetResult();
                if (s.State == "Resumed (queue retained)") resumed.TrySetResult();
            });
        var run = runner.RunAsync(cancellation.Token);
        try
        {
            while (!media.Snapshot().IsPlaying || media.Snapshot().Position < 1.12)
            {
                if (run.IsCompleted) await run;
                await Task.Delay(5, cancellation.Token);
            }
            media.SetPlaying(false);
            await WaitFor(paused.Task, run);
            var session = client.Session;
            await Task.Delay(200, cancellation.Token);
            var watch = Stopwatch.StartNew();
            media.SetPlaying(true);
            await WaitFor(resumed.Task, run);
            Console.WriteLine($"Retained queue resume completed in {watch.Elapsed.TotalMilliseconds:F2} ms; session={session}");
            Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(250), $"Resume took {watch.Elapsed.TotalMilliseconds} ms");
            Assert.Equal(session, client.Session);
            Assert.Single(firmware.Requests.Where(f => f[0] == 1));
            Assert.Single(firmware.Requests.Where(f => f[0] == 7));
            Assert.Single(firmware.Requests.Where(f => f[0] == 8));
            Assert.DoesNotContain(firmware.Requests, f => f[0] == 5);
            await Task.Delay(1000, cancellation.Token);
            if (run.IsCompleted) await run;
            Assert.Equal(0UL, client.Status[21]);
        }
        finally { cancellation.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
        firmware.Close();
        var rows = firmware.ReadTrace();
        var begin = firmware.Requests.Single(f => f[0] == 1);
        Assert.Contains(rows, r => r[3] == 6);
        Assert.Contains(rows, r => r[3] == 7);
        Assert.All(rows, r => Assert.Equal(0, r[5]));
        for (var axis = 0; axis < 6; axis++)
        {
            var offset = 7 + (axis < 3 ? 0 : 3);
            var v = begin[offset] / 1e6; var a = begin[offset + 1] / 1e6; var j = begin[offset + 2] / 1e6;
            foreach (var pair in rows.Zip(rows.Skip(1)))
            {
                Assert.InRange(Math.Abs(pair.Second[6 + axis * 4] - pair.First[6 + axis * 4]), 0, v * .001 + .0005);
                Assert.InRange(Math.Abs(pair.Second[7 + axis * 4] - pair.First[7 + axis * 4]), 0, a * .001 + .001);
                Assert.InRange(Math.Abs(pair.Second[8 + axis * 4] - pair.First[8 + axis * 4]), 0, j * .001 + .01);
            }
        }
    }

    [NativeP4Fact]
    public async Task SixAxisPlaybackMatchesProcessedNativeTrajectoryAndHoldsThenStops()
    {
        using var firmware = new NativeP4();
        await using var client = firmware.Client();
        var media = new Media([new(0, .5), new(1, .52), new(2, .48), new(3, .5)]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new TCodeBufferedPlayback(client, media.Snapshot, media.SetPlaying,
            () => 0, () => true, status => { if (status.State == "Complete (holding)") completed.TrySetResult(); });
        var run = runner.RunAsync(cancellation.Token);
        try
        {
            await WaitFor(completed.Task, run);
            Assert.Equal(0UL, client.Status[21]);
        }
        finally
        {
            cancellation.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.False(media.Snapshot().IsPlaying);
        Assert.Equal(0UL, client.Status[5]);
        firmware.Close();
        var rows = firmware.ReadTrace();
        // The three-second script must be sampled in full; startup no longer
        // contributes the old fixed four seconds to the trace length.
        Assert.True(rows.Count > 3000, "Native motion task must sample the complete script at 1 kHz.");
        Assert.All(rows.Zip(rows.Skip(1)), pair => Assert.Equal(1000, pair.Second[0] - pair.First[0]));
        Assert.All(rows, row => Assert.Equal(0, row[5])); // production smoother rejection counter
        Assert.Contains(rows, row => row[3] == 2 && row[4] == 63);
        Assert.Contains(rows, row => row[3] == 3 && row[4] == 63);
        Assert.Contains(rows, row => row[3] == 4 && row[4] == 63);
        var begin = firmware.Requests.Single(fields => fields[0] == 1);
        Assert.Equal(13, begin.Length);
        Assert.Equal(1UL, begin[6]); // septic profile
        Assert.All(begin.Skip(7), value => Assert.InRange(value, 1UL, 100000000000UL));
        for (var axis = 0; axis < 6; axis++)
        {
            var points = new List<(double Time, double Value)> { (begin[5], .5) };
            foreach (var request in firmware.Requests.Where(fields => fields[0] == 2))
                for (var offset = 4; offset < request.Length; offset += 3)
                    if ((int)request[offset] == axis)
                        points.Add((request[offset + 1], (double)((float)request[offset + 2] * .0001f)));
            var playing = rows.Where(row => row[3] is 3 or 4 && row[0] >= begin[5]).ToArray();
            Assert.True(playing.Length > 2500);
            var values = playing.Select(row => row[6 + axis * 4]).ToArray();
            Assert.True(values.Max() - values.Min() > .039, "Each axis must execute the script, not just ACK it.");
            foreach (var row in playing)
            {
                var left = points.FindLastIndex(point => point.Time <= row[0]);
                left = Math.Max(0, left);
                var a = points[left];
                var b = points[Math.Min(left + 1, points.Count - 1)];
                var u = a.Time < b.Time ? Math.Clamp((row[0] - a.Time) / (b.Time - a.Time), 0, 1) : 0;
                var s = u <= .5 ? Septic(u) : 1 - Septic(1 - u);
                var expected = a.Value + (b.Value - a.Value) * s;
                Assert.InRange(Math.Abs(row[6 + axis * 4] - expected), 0, .000003);
                Assert.InRange(Math.Abs(row[7 + axis * 4]), 0, begin[7 + (axis < 3 ? 0 : 3) + 0] / 1e6 * 1.00001);
                Assert.InRange(Math.Abs(row[8 + axis * 4]), 0, begin[7 + (axis < 3 ? 0 : 3) + 1] / 1e6 * 1.00001);
                Assert.InRange(Math.Abs(row[9 + axis * 4]), 0, begin[7 + (axis < 3 ? 0 : 3) + 2] / 1e6 * 1.00001);
            }
        }
    }

    [NativeP4Fact]
    public async Task FastScriptRetainsAmplitudeWithItsOwnNativeSessionEnvelope()
    {
        using var firmware = new NativeP4();
        await using var client = firmware.Client();
        // Leave time for fragmented clock synchronization before the fast segment.
        var media = new Media([new(0, .5), new(2, .5), new(2.1, .9), new(2.2, .5)]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BufferedSessionLimits limits = null;
        var runner = new TCodeBufferedPlayback(client, media.Snapshot, media.SetPlaying,
            () => 0, () => true, status =>
            {
                if (status.State == "Preloading") limits = status.Limits;
                if (status.State == "Complete (holding)") completed.TrySetResult();
            });
        var run = runner.RunAsync(cancellation.Token);
        try
        {
            await WaitFor(completed.Task, run);
            Assert.NotNull(limits);
            Assert.True(limits.Translation.Jerk > 20000);
            Assert.Equal(0UL, client.Status[21]);
        }
        finally
        {
            cancellation.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
        firmware.Close();
        var rows = firmware.ReadTrace();
        Assert.All(rows, row => Assert.Equal(0, row[5]));
        var moving = rows.Where(row => row[3] is 3 or 4).ToArray();
        Assert.NotEmpty(moving);
        for (var axis = 0; axis < 6; axis++)
        {
            var values = moving.Select(row => row[6 + axis * 4]).ToArray();
            Assert.InRange(values.Max() - values.Min(), .39, .401);
            Assert.All(moving, row =>
            {
                Assert.InRange(Math.Abs(row[7 + axis * 4]), 0, limits.ForAxis(axis).Velocity * 1.00001);
                Assert.InRange(Math.Abs(row[8 + axis * 4]), 0, limits.ForAxis(axis).Acceleration * 1.00001);
                Assert.InRange(Math.Abs(row[9 + axis * 4]), 0, limits.ForAxis(axis).Jerk * 1.00001);
            });
        }
    }

    [NativeP4Fact]
    public async Task FragmentedSerialAndLostBeginAckRetryThroughRealNativeReceiver()
    {
        using var firmware = new NativeP4 { DropFirstBeginAck = true };
        // Windows timer granularity can make seven-byte writes take >100 ms.
        await using var client = firmware.Client(TimeSpan.FromMilliseconds(500));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await client.SynchronizeAsync(timeout.Token);
        await client.QueryPoseAsync(timeout.Token);
        var start = client.Now + 3000000;
        await client.BeginAsync(1, start, new(new(1, 4, 20), new(1, 4, 20)), timeout.Token);
        var begins = firmware.Wires.Where(wire => TCodeBufferedProtocol.TryDecode(wire, out var f) && f[0] == 1).ToArray();
        Assert.Equal(2, begins.Length);
        Assert.Equal(begins[0], begins[1]);
        Assert.Equal(1UL, client.Session);
        Assert.Equal(511UL, client.Status[9]);
        await client.SendAsync(2, timeout.Token, 0, start + 1000000, 5100);
        await client.SendAsync(3, timeout.Token, 1);
        await client.SendAsync(4, timeout.Token);
        await client.StopAsync(timeout.Token);
        Assert.Equal(0UL, client.Status[5]);
    }

    private static double Septic(double u) => u * u * u * u * (35 + u * (-84 + u * (70 - 20 * u)));
    private static async Task WaitFor(Task target, Task run)
    {
        var winner = await Task.WhenAny(target, run).WaitAsync(TimeSpan.FromSeconds(15));
        if (winner == run) { await run; Assert.Fail("Playback ended before completion."); }
        await target;
    }

    private sealed class NativeP4 : IDisposable
    {
        private readonly string _trace;
        private readonly bool _keepTrace;
        private readonly Process _process;
        private readonly Task<string> _errors;
        private string _pending = "";
        private ulong? _beginRequest;
        private bool _dropped;
        private bool _closed;
        public bool DropFirstBeginAck { get; init; }
        public bool FragmentWrites { get; init; } = true;
        public List<ulong[]> Requests { get; } = [];
        public List<string> Wires { get; } = [];
        public NativeP4(bool injectPauseStall = false)
        {
            var directory = Environment.GetEnvironmentVariable("MFP_NATIVE_TRACE_DIR");
            _keepTrace = !string.IsNullOrEmpty(directory);
            directory = _keepTrace ? directory : Path.GetTempPath();
            Directory.CreateDirectory(directory);
            _trace = Path.Combine(directory, $"mfp-native-p4-{Guid.NewGuid():N}.csv");
            var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("CTW_NATIVE_P4"))
            { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            info.ArgumentList.Add("--trace");
            info.ArgumentList.Add(_trace);
            if (injectPauseStall) info.ArgumentList.Add("--inject-pause-stall");
            _process = Process.Start(info);
            _errors = _process.StandardError.ReadToEndAsync();
        }
        public TCodeBufferedClient Client(TimeSpan? timeout = null, Action<string> diagnostic = null, TrackingMonitor tracking = null) => new(async (wire, ct) =>
        {
            Assert.True(TCodeBufferedProtocol.TryDecode(wire, out var fields));
            Requests.Add(fields);
            Wires.Add(wire);
            if (fields[0] == 1) _beginRequest = fields[1];
            if (!FragmentWrites)
            {
                await _process.StandardInput.WriteAsync(wire.AsMemory(), ct);
                await _process.StandardInput.FlushAsync(ct);
                await Task.Delay(10, ct);
                return;
            }
            // Real UART reader receives arbitrary fragments across its task ticks.
            for (var offset = 0; offset < wire.Length; offset += 7)
            {
                await _process.StandardInput.WriteAsync(wire.AsMemory(offset, Math.Min(7, wire.Length - offset)), ct);
                await _process.StandardInput.FlushAsync(ct);
                await Task.Delay(1, ct);
            }
        }, async ct =>
        {
            while (_pending.Length == 0)
            {
                var line = await _process.StandardOutput.ReadLineAsync(ct);
                if (line == null) return null;
                if (DropFirstBeginAck && !_dropped && TCodeBufferedProtocol.TryDecode(line, out var fields)
                    && fields[1] == _beginRequest)
                { _dropped = true; continue; }
                _pending = line + "\n";
            }
            var chunk = _pending[..Math.Min(11, _pending.Length)];
            _pending = _pending[chunk.Length..];
            return chunk;
        }, timeout, diagnostic, tracking: tracking);
        public void Close()
        {
            if (_closed) return;
            _process.StandardInput.Close();
            if (!_process.WaitForExit(3000)) { _process.Kill(); throw new TimeoutException("Native P4 did not exit."); }
            _closed = true;
            Assert.True(_process.ExitCode == 0, _errors.GetAwaiter().GetResult());
        }
        public List<double[]> ReadTrace()
            => File.ReadLines(_trace).Skip(1).Select(line => line.Split(',').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray()).ToList();
        public void Dispose()
        {
            try { Close(); }
            finally
            {
                _process.Dispose();
                if (_keepTrace) File.WriteAllLines(Path.ChangeExtension(_trace, ".wire"), Wires);
                else File.Delete(_trace);
            }
        }
    }

    private sealed class Media(Keyframe[] points)
    {
        private readonly object _gate = new();
        private bool _playing = true;
        private double _position;
        private long _timestamp = Stopwatch.GetTimestamp();
        public BufferedPlaybackSnapshot Snapshot()
        {
            lock (_gate)
            {
                Update();
                return new(_playing, _position, 1, 0,
                    TCodeBufferedPlan.AxisNames.Select(name => new BufferedScriptAxis(name, points, 0, 1, false, .5)).ToArray());
            }
        }
        public void SetPlaying(bool value) { lock (_gate) { Update(); _playing = value; } }
        private void Update()
        {
            var now = Stopwatch.GetTimestamp();
            if (_playing) _position += (now - _timestamp) / (double)Stopwatch.Frequency;
            _timestamp = now;
        }
    }
}
