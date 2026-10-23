using MultiFunPlayer.Common;
using MultiFunPlayer.OutputTarget.TCodeBuffered;
using System.Collections.Concurrent;
using System.Diagnostics;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MultiFunPlayer.TCode.Tests;

public sealed class FirmwareFactAttribute : FactAttribute
{
    public FirmwareFactAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("CTW_TCODE_HARNESS")))
            Skip = "Set CTW_TCODE_HARNESS to FirmwareHarness compiled against CTW's actual tcode_queue.hpp.";
    }
}

public class FirmwareTests
{
    [FirmwareFact]
    public async Task FiveAxisDevicePlaysAllScriptsWithoutRequiringTwistMotor()
    {
        using var firmware = new Firmware(55);
        await using var client = firmware.Client();
        var media = new Media();
        BufferedPlaybackSnapshot Snapshot()
        {
            var source = media.Snapshot();
            return source with { Axes = TCodeBufferedPlan.AxisNames.Select(name => source.Axes[0] with { Name = name }).ToArray() };
        }
        var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var diagnostics = new ConcurrentQueue<string>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var runner = new TCodeBufferedPlayback(client, Snapshot, media.SetPlaying, () => 0, () => true,
            status => { if (status.State == "Playing") playing.TrySetResult(); }, diagnostics.Enqueue);
        var run = runner.RunAsync(cancellation.Token);
        try
        {
            var completed = await Task.WhenAny(playing.Task, run).WaitAsync(TimeSpan.FromSeconds(5));
            if (completed == run) await run;
            Assert.True(playing.Task.IsCompletedSuccessfully);
            Assert.Contains(diagnostics, line => line.Contains("supportedAxes=55 ignoredAxes=[R0]"));
            Assert.Equal(1UL, client.Session);
        }
        finally { cancellation.Cancel(); await run; }
        Assert.False(client.MayOwnMotion);
    }
    [FirmwareFact]
    public async Task PreparationReplansWithoutBrieflyPlayingAndResumesPromptly()
    {
        using var firmware = new Firmware();
        await using var client = firmware.Client();
        var media = new Media();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var plays = 0;
        var preparations = 0;
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = Stopwatch.StartNew();
        var runner = new TCodeBufferedPlayback(client, media.Snapshot,
            playing => { if (playing) Interlocked.Increment(ref plays); media.SetPlaying(playing); },
            () => 0, () => true, s =>
            {
                if (s.State == "Reading device pose" && ++preparations == 1)
                    media.Seek(.01, 1); // delayed media position after pause acknowledgement
                if (s.State == "Playing") resumed.TrySetResult();
            });
        var run = runner.RunAsync(cancellation.Token);
        try
        {
            await resumed.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.True(preparations >= 2);
            Assert.Equal(1, plays);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
        }
        finally { cancellation.Cancel(); await run; }
    }

    [FirmwareFact]
    public async Task HostKeepsFullScriptAndDeviceEnforcesSessionEnvelope()
    {
        using var firmware = new Firmware();
        await using var client = firmware.Client();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        await client.SynchronizeAsync(token);
        var pose = await client.QueryPoseAsync(token);
        var plan = TCodeBufferedPlan.CreateSession(ProtocolAndPlanTests.Snapshot([new(0, .82), new(.058, .15), new(.116, .82)]), pose);
        Assert.Equal(1500UL, plan.Nodes[0][1].Position);
        Assert.True(plan.Limits.Translation.Jerk > 100000);
        var start = client.Now + 3000000;
        await client.BeginAsync(1, start, plan.Limits, token);
        foreach (var node in plan.Nodes[0])
            await client.SendAsync(2, token, 0, start + node.Microseconds, node.Position);
        await client.SendAsync(3, token, 1);
        await client.SendAsync(4, token);
        await client.StopAsync(token);
        await client.QueryPoseAsync(token);
        start = client.Now + 3000000;
        await client.BeginAsync(1, start, new(new(1, 4, 20), new(1, 4, 20)), token);
        var error = await Assert.ThrowsAsync<TCodeBufferedException>(() => client.SendAsync(2, token, 0, start + 1000, 9999));
        Assert.Equal(9UL, error.Code);
        await client.SendAsync(0, token);
        Assert.Equal(511UL, client.Status[9]);
        await client.StopAsync(token);
    }

    [FirmwareFact]
    public async Task ActualP4AcceptsHandshakeAppendSealArmAndStop()
    {
        using var firmware = new Firmware();
        await using var client = firmware.Client();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        await client.SynchronizeAsync(token);
        await client.QueryPoseAsync(token);
        var start = client.Now + 3000000;
        await client.BeginAsync(0x37, start, new(new(1, 4, 20), new(1, 4, 20)), token); // explicitly exclude R0
        await client.SendAsync(2, token, 0, start + 1000000, 5100,
            1, start + 1000000, 5000, 2, start + 1000000, 5000,
            4, start + 1000000, 4900, 5, start + 1000000, 5000);
        await client.SendAsync(3, token, 0x37);
        await client.SendAsync(4, token);
        Assert.Equal(2UL, client.Status[5]);
        Assert.Equal(510UL, client.Status[9]);
        Assert.Equal(512UL, client.Status[12]);
        await client.StopAsync(token);
        Assert.Equal(0UL, client.Status[5]);
        Assert.Equal(0UL, client.Status[21]);
    }

    [FirmwareFact]
    public async Task CancellationStopsAnArmedQueueWithoutUsingCancelledToken()
    {
        using var firmware = new Firmware();
        await using var client = firmware.Client();
        var media = new Media();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var positioned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new TCodeBufferedPlayback(client, media.Snapshot, media.SetPlaying,
            () => 0, () => true, s => { if (s.State == "Positioning") positioned.TrySetResult(); });
        var run = runner.RunAsync(cancellation.Token);
        await positioned.Task.WaitAsync(TimeSpan.FromSeconds(8));
        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        await client.SendAsync(0, CancellationToken.None);
        Assert.Equal(0UL, client.Status[5]);
        Assert.False(media.Snapshot().IsPlaying);
    }

    [FirmwareFact]
    public async Task PlaybackRefillsThenPauseSeekAndSpeedChangeCreateFreshSessions()
    {
        using var firmware = new Firmware();
        await using var client = firmware.Client();
        var media = new Media();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var states = new ConcurrentQueue<string>();
        var runner = new TCodeBufferedPlayback(client, media.Snapshot, media.SetPlaying,
            () => 0, () => true, s => states.Enqueue(s.State));
        var run = runner.RunAsync(cancellation.Token);
        try
        {
            await WaitFor(() => states.Contains("Playing"), run);
            var session = client.Session;
            var initialTail = client.Status[15];
            await WaitFor(() => client.Status[15] > initialTail, run);
            media.SetPlaying(false);
            await WaitFor(() => client.Status[5] == 7, run);
            Assert.Equal(0UL, client.Status[21]);
            media.Seek(10, 2);
            media.SetPlaying(true);
            await WaitFor(() => client.Session > session && client.Status[5] == 3, run);
            Assert.Equal(0UL, client.Status[21]);
        }
        finally
        {
            cancellation.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(0UL, client.Status[5]);
    }

    [FirmwareFact]
    public async Task ExplicitPauseDuringPreloadNeverAutoResumesMedia()
    {
        using var firmware = new Firmware();
        await using var client = firmware.Client();
        var media = new Media();
        long controls = 0;
        var wantsPlayback = true;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var positioned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new TCodeBufferedPlayback(client, media.Snapshot, media.SetPlaying,
            () => Interlocked.Read(ref controls), () => wantsPlayback,
            s => { if (s.State == "Positioning") positioned.TrySetResult(); });
        var run = runner.RunAsync(cancellation.Token);
        await positioned.Task.WaitAsync(TimeSpan.FromSeconds(8));
        wantsPlayback = false;
        Interlocked.Increment(ref controls);
        await WaitFor(() => client.Status[5] == 0, run);
        Assert.False(media.Snapshot().IsPlaying);
        cancellation.Cancel();
        await run;
    }

    [FirmwareFact]
    public async Task MediaClockDriftPausesMediaAndStopsDevice()
    {
        using var firmware = new Firmware();
        await using var client = firmware.Client();
        var media = new Media();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var states = new ConcurrentQueue<string>();
        var diagnostics = new ConcurrentQueue<string>();
        var runner = new TCodeBufferedPlayback(client, media.Snapshot, media.SetPlaying,
            () => 0, () => true, s => states.Enqueue(s.State), diagnostics.Enqueue);
        var run = runner.RunAsync(cancellation.Token);
        await WaitFor(() => states.Contains("Playing"), run);
        media.Drift(1);
        var error = await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("150 ms", error.Message);
        Assert.Contains("expected=", error.Message);
        Assert.Contains("drift=", error.Message);
        var clockError = Assert.Single(diagnostics.Where(s => s.StartsWith("CLOCK ERROR")));
        Assert.Contains("actualSeconds=", clockError);
        Assert.Contains("expectedSeconds=", clockError);
        Assert.Contains("direction=media-ahead", clockError);
        Assert.Contains("deviceNowUs=", clockError);
        Assert.Contains("status=[", clockError);
        Assert.Contains(diagnostics, s => s.StartsWith("CLOCK SAMPLE"));
        Assert.Equal(0UL, client.Status[5]);
        Assert.False(media.Snapshot().IsPlaying);
    }

    [FirmwareFact]
    public async Task MediaWithoutPauseControlNeverAcquiresDeviceMotion()
    {
        using var firmware = new Firmware();
        await using var client = firmware.Client();
        var media = new Media();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var runner = new TCodeBufferedPlayback(client, media.Snapshot, _ => { },
            () => 0, () => true, _ => { });
        var error = await Assert.ThrowsAsync<IOException>(() => runner.RunAsync(cancellation.Token));
        Assert.Contains("acknowledge pause", error.Message);
        Assert.False(client.MayOwnMotion);
        Assert.Equal(0UL, client.Session);
    }

    private static async Task WaitFor(Func<bool> condition, Task run)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (run.IsCompleted) { await run; Assert.Fail("Playback ended before the expected state."); }
            if (deadline.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException("Expected playback state was not reached.");
            await Task.Delay(10);
        }
    }

    private sealed class Firmware : IDisposable
    {
        private readonly Process _process;
        public Firmware(int supportedAxes = 63)
        {
            _process = Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("CTW_TCODE_HARNESS"))
            { Arguments = supportedAxes.ToString(), RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false });
        }
        public TCodeBufferedClient Client() => new(async (wire, ct) =>
        {
            await _process.StandardInput.WriteAsync(wire.AsMemory(), ct);
            await _process.StandardInput.FlushAsync(ct);
        }, async ct =>
        {
            var line = await _process.StandardOutput.ReadLineAsync(ct);
            return line == null ? null : line + "\n";
        });
        public void Dispose()
        {
            _process.StandardInput.Close();
            if (!_process.WaitForExit(1000)) _process.Kill();
            _process.Dispose();
        }
    }

    private sealed class Media
    {
        private readonly object _gate = new();
        private readonly Keyframe[] _points = Enumerable.Range(0, 101).Select(i => new Keyframe(i * .5, i % 2 == 0 ? .45 : .55)).ToArray();
        private bool _playing = true;
        private double _position;
        private double _speed = 1;
        private long _revision;
        private long _timestamp = Stopwatch.GetTimestamp();
        public BufferedPlaybackSnapshot Snapshot()
        {
            lock (_gate)
            {
                Update();
                return new(_playing, _position, _speed, _revision, [new("L0", _points, 0, 1, false, .5)]);
            }
        }
        public void Drift(double seconds) { lock (_gate) { Update(); _position += seconds; } }
        public void SetPlaying(bool value) { lock (_gate) { Update(); _playing = value; } }
        public void Seek(double position, double speed) { lock (_gate) { Update(); _position = position; _speed = speed; _revision++; } }
        private void Update()
        {
            var now = Stopwatch.GetTimestamp();
            if (_playing) _position += (now - _timestamp) / (double)Stopwatch.Frequency * _speed;
            _timestamp = now;
        }
    }
}
