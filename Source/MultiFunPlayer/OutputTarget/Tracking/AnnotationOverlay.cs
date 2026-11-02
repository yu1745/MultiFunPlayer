using System.Collections.Concurrent;
using System.IO;
namespace MultiFunPlayer.OutputTarget.Tracking;

// Read-only display index. Loading history runs away from motion/UI threads.
internal static class AnnotationOverlay
{
    private static readonly ConcurrentDictionary<(string Folder,string Id),AnnotationStore.Entry> entries = new();
    private static readonly ConcurrentDictionary<(string Folder,string Id),bool> removed = new();
    public static void Add(string folder, AnnotationStore.Entry entry)
    { if(folder != null && double.IsFinite(entry.Seconds)) entries[(folder,entry.Id)] = entry; }
    public static void Remove(string folder,string id)
    { removed[(folder,id)] = true; entries.TryRemove((folder,id),out _); }
    public static AnnotationStore.Entry[] ForVideo(string video) => string.IsNullOrWhiteSpace(video) ? [] : entries
        .Where(p=>!removed.ContainsKey(p.Key) && string.Equals(p.Value.Video,video,StringComparison.OrdinalIgnoreCase))
        .Select(p=>p.Value).OrderBy(p=>p.Seconds).ToArray();
    public static int LoadHistory(string root)
    {
        if(!Directory.Exists(root)) return 0;
        var failures=0;
        foreach(var file in Directory.EnumerateFiles(root,"events.jsonl",SearchOption.AllDirectories))
        {
            try { var folder=Path.GetDirectoryName(file); foreach(var entry in AnnotationStore.Load(folder)) Add(folder,entry); }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { failures++; }
        }
        return failures;
    }
}
