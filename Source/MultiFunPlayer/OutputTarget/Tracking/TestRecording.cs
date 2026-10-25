using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace MultiFunPlayer.OutputTarget.Tracking;

// Optional, read-only recorder. Never waits for disk on the serial/motion threads.
internal static class TestRecording
{
    private sealed class Session(string directory)
    {
        public readonly string Directory = directory;
        public readonly Channel<string> Queue = Channel.CreateBounded<string>(new BoundedChannelOptions(8192)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        public Task Worker;
        public long Dropped;
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> States = new();
    }
    private static readonly object Gate = new();
    private static Session current;
    private static string error;
    private static Task draining = Task.CompletedTask;
    private static long lastTelemetry;
    private static readonly JsonSerializerOptions Options = new()
    { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    public static bool Active => Volatile.Read(ref current) != null;
    public static string DirectoryPath { get; private set; }
    public static string Status
    {
        get { var s = Volatile.Read(ref current); return error ?? (s != null ? $"记录中 · 日志丢弃 {Interlocked.Read(ref s.Dropped)} 条 · " + ((Stopwatch.GetTimestamp() - Interlocked.Read(ref lastTelemetry)) / (double)Stopwatch.Frequency < 1 ? "已收到电机反馈" : "等待电机反馈（检查 Buffered Update 和跟随监测）") : "已停止 / 未记录"); }
    }
    public static void State(string key, object data)
    {
        var s = Volatile.Read(ref current);
        if (s == null) return;
        try
        {
            var json = JsonSerializer.Serialize(data, Options);
            if (s.States.TryGetValue(key, out var previous) && previous == json) return;
            s.States[key] = json;
            Record("state", new { key, value = JsonSerializer.Deserialize<JsonElement>(json) });
        }
        catch (Exception ex) { error = "状态记录失败：" + ex.Message; }
    }
    public static void Start(string root)
    {
        lock (Gate)
        {
            if (current != null) return;
            var folder = Path.Combine(root, DateTime.Now.ToString("yyyy-MM-dd"), DateTime.Now.ToString("HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
            System.IO.Directory.CreateDirectory(folder);
            // Check write permission before reporting recording as started.
            var stream = new StreamWriter(new FileStream(Path.Combine(folder, "events.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read));
            var session = new Session(folder);
            error = null; Interlocked.Exchange(ref lastTelemetry, 0); DirectoryPath = folder;
            current = session;
            session.Worker = Task.Run(() => WriteAsync(session, stream));
            Record("start", new { schema = 2, player = typeof(TestRecording).Assembly.GetName().Version?.ToString(),
                sourceCommit = "642ce7d+motor-health-v2", timebase = "hostSeconds=Stopwatch; packet.TimeMicroseconds=P4; Command/Feedback=radians", 
                motorHealth = new { protocol = "MTR1", optional = true, units = new[] { "rad/s", "Iq A", "FET C", "motor C", "bus V" }, missing = "valid mask + per-field reception age; no sensor value assumed" } });
        }
    }
    public static string Annotate(string label, string note, double mediaSeconds, bool isPlaying, string settings)
    {
        var id = Guid.NewGuid().ToString("N");
        Record("annotation", new { annotationId = id, label, note, mediaSeconds, paused = !isPlaying,
            MediaPosition = mediaSeconds, IsPlaying = isPlaying, settings });
        return id;
    }
    public static void Record(string kind, object data)
    {
        var session = Volatile.Read(ref current);
        if (session == null) return;
        try
        {
            if (kind == "tracking") Interlocked.Exchange(ref lastTelemetry, Stopwatch.GetTimestamp());
            var line = JsonSerializer.Serialize(new { kind, utc = DateTimeOffset.UtcNow, hostSeconds = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency, data }, Options);
            if (!session.Queue.Writer.TryWrite(line)) Interlocked.Increment(ref session.Dropped);
        }
        catch (Exception ex) { error = "日志编码失败：" + ex.Message; Interlocked.Increment(ref session.Dropped); }
    }
    public static async Task StopAsync()
    {
        Task completion;
        lock (Gate)
        {
            if (current != null)
            {
                var session = current;
                current = null;
                session.Queue.Writer.TryComplete();
                draining = Task.WhenAll(draining, session.Worker);
            }
            completion = draining;
        }
        await completion.ConfigureAwait(false);
    }
    private static async Task WriteAsync(Session session, StreamWriter stream)
    {
        try
        {
            using (stream)
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
                while (true)
                {
                    // A bounded batch ensures flushes also happen under continuous traffic.
                    for (var n = 0; n < 8192 && session.Queue.Reader.TryRead(out var line); n++)
                        await stream.WriteLineAsync(line).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                    if (session.Queue.Reader.Completion.IsCompleted) break;
                    await timer.WaitForNextTickAsync().ConfigureAwait(false);
                }
                await stream.WriteLineAsync(JsonSerializer.Serialize(new { kind = "stop", utc = DateTimeOffset.UtcNow, dropped = session.Dropped }, Options)).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            error = "记录失败（动作输出不受此日志控制）：" + ex.Message;
            lock (Gate) { if (ReferenceEquals(current, session)) current = null; }
            session.Queue.Writer.TryComplete();
        }
    }
}
