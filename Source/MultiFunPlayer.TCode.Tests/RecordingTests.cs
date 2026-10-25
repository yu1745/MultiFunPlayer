using System.Text.Json;
using MultiFunPlayer.OutputTarget.Tracking;
namespace MultiFunPlayer.TCode.Tests;
[CollectionDefinition("Recorder", DisableParallelization = true)]
public class RecorderCollection { }
[Collection("Recorder")]
public class RecordingTests
{
    [Fact]
    public async Task RecordsRawFeedbackAndMarkersThenDrainsAndStartsSeparateRun()
    {
        var root = Path.Combine(Path.GetTempPath(), "mfp-record-test-" + Guid.NewGuid().ToString("N"));
        TestRecording.Start(root);
        var first = TestRecording.DirectoryPath;
        try
        {
            var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "TestData", "tracking-cad.frames"));
            using var monitor = new TrackingMonitor();
            foreach (var line in lines)
            {
                Assert.True(TrackingPacket.TryDecode(line, out var packet, out _));
                monitor.Accept(packet, packet.TimeMicroseconds + 1000, 1);
            }
            TestRecording.Record("annotation", new { path = @"E:\任意目录\测试.funscript", mediaSeconds = 12.5, text = "变向异响" });
            TestRecording.Record("invalid_value", new { value = double.NaN });
            TestRecording.State("range", new { min = .1, max = .9 });
            TestRecording.State("range", new { min = .1, max = .9 });
            TestRecording.State("range", new { min = .2, max = .8 });
            await TestRecording.StopAsync();
            var records = File.ReadAllLines(Path.Combine(first, "events.jsonl")).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToArray();
            Assert.Equal(lines.Length, records.Count(r => r.GetProperty("kind").GetString() == "tracking"));
            Assert.Equal(2, records.Count(r => r.GetProperty("kind").GetString() == "state"));
            Assert.Equal("stop", records[^1].GetProperty("kind").GetString());
            Assert.Equal(0, records[^1].GetProperty("dropped").GetInt64());
            var mark = records.Single(r => r.GetProperty("kind").GetString() == "annotation");
            Assert.Equal(12.5, mark.GetProperty("data").GetProperty("mediaSeconds").GetDouble());
            Assert.Contains("任意目录", mark.GetProperty("data").GetProperty("path").GetString());
            TestRecording.Start(root);
            Assert.NotEqual(first, TestRecording.DirectoryPath);
        }
        finally { await TestRecording.StopAsync(); }
    }
    [Fact]
    public async Task ConcurrentProducersAccountForEveryRecordAndRepeatedStopWaitsForDrain()
    {
        TestRecording.Start(Path.Combine(Path.GetTempPath(), "mfp-burst-" + Guid.NewGuid().ToString("N")));
        var folder = TestRecording.DirectoryPath;
        await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(() => {
            for (var j = 0; j < 5000; j++) TestRecording.Record("burst", new { producer = i, sequence = j });
        })));
        await Task.WhenAll(TestRecording.StopAsync(), TestRecording.StopAsync());
        var rows = File.ReadAllLines(Path.Combine(folder, "events.jsonl")).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToArray();
        Assert.Equal("stop", rows[^1].GetProperty("kind").GetString());
        Assert.Equal(20000, rows.Count(r => r.GetProperty("kind").GetString() == "burst") + rows[^1].GetProperty("dropped").GetInt64());
    }
    [Fact]
    public void FailedStartDoesNotClaimToRecord()
    {
        var file = Path.Combine(Path.GetTempPath(), "mfp-file-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, "not a directory");
        Assert.ThrowsAny<IOException>(() => TestRecording.Start(file));
        Assert.False(TestRecording.Active);
    }
}
