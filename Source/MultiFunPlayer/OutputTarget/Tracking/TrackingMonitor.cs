using System.Diagnostics;
using System.Threading.Channels;

namespace MultiFunPlayer.OutputTarget.Tracking;

// The receiver only enqueues immutable packets. FK and window statistics run
// here, never on the serial reader or WPF dispatcher. Old snapshots can drop.
internal sealed class TrackingMonitor : IDisposable
{
    private sealed record Message(TrackingPacket Packet = null, double HostSeconds = 0, bool Corrupt = false,
        bool Reset = false, double? Window = null, double? DeliveryAgeMs = null, double? ClockUncertaintyMs = null);
    private readonly Channel<Message> queue = Channel.CreateBounded<Message>(new BoundedChannelOptions(128)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task worker;
    private TrackingSnapshot snapshot = TrackingSnapshot.Empty();
    private string availability = "请连接串口并使用 Buffered Update";
    private int disposed;
    public static double HostSeconds => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    private readonly MotorPowerObservation powerObservation = new();
    private MotorHealthSnapshot motorHealth;
    private string motorAvailability = "等待电机状态订阅";
    public MotorHealthSnapshot MotorHealth => Volatile.Read(ref motorHealth);
    public string MotorAvailability => Volatile.Read(ref motorAvailability);
    public void SetMotorAvailability(string value) => Volatile.Write(ref motorAvailability, value);
    public void AcceptMotor(MotorHealthPacket packet, double deviceNowUs)
    {
        var previous = MotorHealth;
        if (previous != null && previous.Packet.Boot == packet.Boot &&
            (unchecked((int)(packet.Sequence - previous.Packet.Sequence)) <= 0 || packet.TimeMicroseconds <= previous.Packet.TimeMicroseconds))
        { TestRecording.Record("motor_health_reordered", new { packet.Sequence }); return; }
        var deliveryAgeMs = Math.Max(0, (deviceNowUs - packet.TimeMicroseconds) / 1000);
        powerObservation.Observe(packet, HostSeconds, DateTimeOffset.UtcNow, deliveryAgeMs);
        TestRecording.Record("motor_health", new { packet, deliveryAgeMs, speedUnit = "rad/s", currentUnit = "Iq A", temperatureUnit = "C", voltageUnit = "V" });
        Volatile.Write(ref motorHealth, new MotorHealthSnapshot(packet, HostSeconds, deliveryAgeMs));
        SetMotorAvailability(null);
    }
    public TrackingSnapshot Snapshot => Volatile.Read(ref snapshot);
    public string Availability => Volatile.Read(ref availability);
    public bool IsLive
    {
        get
        {
            var q = Snapshot.Quality;
            var elapsed = HostSeconds - q.LastPacketHostSeconds;
            return Availability == null && q.LastPacketHostSeconds > 0 && elapsed <= .5 &&
                elapsed * 1000 + (q.DeliveryAgeMs ?? 0) <= 500 + (q.ClockUncertaintyMs ?? 0);
        }
    }
    public TrackingMonitor() => worker = Task.Run(RunAsync);
    public void BeginConnection() { powerObservation.Reset(); Volatile.Write(ref motorHealth, null); SetMotorAvailability("等待电机状态订阅"); queue.Writer.TryWrite(new(Reset: true)); SetAvailability("正在订阅实时跟随数据…"); }
    public void SetAvailability(string reason) => Volatile.Write(ref availability, reason);
    public void Accept(TrackingPacket packet, double? deviceNowUs = null, double? clockUncertaintyMs = null)
    {
        TestRecording.Record("tracking", new { packet, deviceNowUs, clockUncertaintyMs });
        queue.Writer.TryWrite(new(packet, HostSeconds, DeliveryAgeMs: deviceNowUs.HasValue ?
            Math.Max(0, (deviceNowUs.Value - packet.TimeMicroseconds) / 1000) : null, ClockUncertaintyMs: clockUncertaintyMs));
        SetAvailability(null);
    }
    public void BadPacket(string reason) { TestRecording.Record("tracking_bad_packet", new { reason }); queue.Writer.TryWrite(new(Corrupt: true)); }
    public void SetWindow(double seconds) => queue.Writer.TryWrite(new(Window: Math.Clamp(seconds, 2, 12)));
    public void Reset() => queue.Writer.TryWrite(new(Reset: true));
    private async Task RunAsync()
    {
        var analysis = new TrackingAnalysis(); var lastPublish = 0d; var lastPowerLog = 0d;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
            while (await timer.WaitForNextTickAsync(cancellation.Token).ConfigureAwait(false))
            {
                var changed = false; var force = false;
                while (queue.Reader.TryRead(out var message))
                {
                    changed = true;
                    if (message.Reset) { analysis.Reset(); force = true; }
                    if (message.Window.HasValue) { analysis.WindowSeconds = message.Window.Value; force = true; }
                    if (message.Corrupt) analysis.Corrupt();
                    if (message.Packet != null) analysis.Add(message.Packet, message.HostSeconds, message.DeliveryAgeMs, message.ClockUncertaintyMs);
                }
                var now = HostSeconds;
                if (now - lastPowerLog >= 1) { TestRecording.Record("motor_power_observation", powerObservation.Snapshot(now)); lastPowerLog = now; }
                if (changed && (now - lastPublish >= .1 || force))
                { Volatile.Write(ref snapshot, analysis.Snapshot()); lastPublish = now; }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { SetAvailability("跟随分析不可用：" + error.Message); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        cancellation.Cancel(); queue.Writer.TryComplete();
        // The worker has no serial or UI ownership. Observe completion without
        // synchronously blocking the UI while an analysis window finishes.
        _ = worker.ContinueWith(_ => cancellation.Dispose(), TaskScheduler.Default);
    }
}

