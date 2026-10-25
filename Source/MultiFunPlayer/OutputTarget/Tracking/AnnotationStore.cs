using System.IO;
using System.Text.Json;
namespace MultiFunPlayer.OutputTarget.Tracking;

// Deletion overlay keeps raw telemetry immutable. Analysis must exclude IDs in this file.
internal static class AnnotationStore
{
    internal sealed record Entry(string Id, string Video, double Seconds, string Label, string Note)
    {
        public string Display => $"{Path.GetFileName(Video)} · {Seconds:F2}s · {Label} · {Note}";
    }
    public static Entry Create(string id, double seconds, string label, string note, string settings)
    {
        using var json = JsonDocument.Parse(settings);
        var video = json.RootElement.TryGetProperty("mediaPath", out var path) ? path.GetString() : null;
        return new(id, video ?? "未加载视频", seconds, label, note);
    }
    private static string DeletionFile(string folder) => Path.Combine(folder, "annotation-deletions.json");
    private static HashSet<string> Deleted(string folder) => File.Exists(DeletionFile(folder))
        ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(DeletionFile(folder))) ?? new() : new();
    public static List<Entry> Load(string folder)
    {
        var deleted = Deleted(folder); var result = new List<Entry>();
        using var stream = new FileStream(Path.Combine(folder, "events.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        long lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            ++lineNumber;
            JsonDocument json;
            try { json = JsonDocument.Parse(line); }
            catch (JsonException) when (reader.EndOfStream) { break; } // active writer's incomplete last line
            using (json)
            {
                var root = json.RootElement;
                if (root.GetProperty("kind").GetString() != "annotation") continue;
                var d = root.GetProperty("data");
                var id = d.TryGetProperty("annotationId", out var key) ? key.GetString() : $"legacy-line-{lineNumber}";
                if (deleted.Contains(id)) continue;
                var seconds = d.TryGetProperty("mediaSeconds", out var time) && time.ValueKind == JsonValueKind.Number ? time.GetDouble() : double.NaN;
                result.Add(Create(id, seconds,
                    d.TryGetProperty("label", out var label) ? label.GetString() : "标注",
                    d.TryGetProperty("note", out var note) ? note.GetString() : "",
                    d.TryGetProperty("settings", out var settings) ? settings.GetString() : "{}"));
            }
        }
        return result;
    }
    public static void Delete(string folder, string id)
    {
        var deleted = Deleted(folder); deleted.Add(id);
        var path = DeletionFile(folder); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(deleted)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
