using System.Text.Json;
using MultiFunPlayer.OutputTarget.Tracking;
namespace MultiFunPlayer.TCode.Tests;
[Collection("Recorder")]
public class AnnotationDeletionTests
{
    [Fact]
    public async Task DeleteOneVideoMarkPreservesOtherAndTelemetry()
    {
        TestRecording.Start(Path.Combine(Path.GetTempPath(), "annotation-" + Guid.NewGuid()));
        var folder = TestRecording.DirectoryPath;
        var a = TestRecording.Annotate("不适", "a", 12, false, "{\"mediaPath\":\"D:/a.mp4\"}");
        var b = TestRecording.Annotate("正常", "b", 12, false, "{\"mediaPath\":\"D:/b.mp4\"}");
        TestRecording.Record("motor_health", new { current = 3 });
        await TestRecording.StopAsync();
        var path = Path.Combine(folder, "events.jsonl");
        var before = File.ReadAllBytes(path);
        Assert.Equal(2, AnnotationStore.Load(folder).Count);
        AnnotationStore.Delete(folder, a);
        AnnotationStore.Delete(folder, a);
        var remaining = Assert.Single(AnnotationStore.Load(folder));
        Assert.Equal(b, remaining.Id);
        Assert.Contains("b.mp4", remaining.Display);
        Assert.Equal(before, File.ReadAllBytes(path));
    }
    [Fact]
    public void LegacyMarksCanBeDeletedWithoutRewritingLog()
    {
        var folder = Path.Combine(Path.GetTempPath(), "annotation-old-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "events.jsonl");
        File.WriteAllText(path, "{\"kind\":\"start\"}\n" + JsonSerializer.Serialize(new { kind = "annotation", data = new { mediaSeconds = 5, label = "错误", note = "", settings = "{\"mediaPath\":\"D:/old.mp4\"}" } }) + "\n");
        var entry = Assert.Single(AnnotationStore.Load(folder));
        Assert.Equal("legacy-line-2", entry.Id);
        AnnotationStore.Delete(folder, entry.Id);
        Assert.Empty(AnnotationStore.Load(folder));
        Assert.Equal(2, File.ReadAllLines(path).Length);
    }
}
