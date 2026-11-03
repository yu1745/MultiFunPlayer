using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
namespace MultiFunPlayer.OutputTarget.Tracking;

internal sealed class FilterRuntime : INotifyPropertyChanged
{
    public static FilterRuntime Instance { get; } = new();
    public event PropertyChangedEventHandler PropertyChanged;
    private string text="滤波关闭", color="#808080";
    public string Text => text;
    public string Color => color;
    public static int BufferedConnections;
    public static bool Enabled = false;
    public static void Status(string state,bool pending=false)
    {
        Instance.text=state;Instance.color=!Enabled?"#808080":pending?"#D89C20":"#35A56B";
        Instance.PropertyChanged?.Invoke(Instance,new(nameof(Text)));
        Instance.PropertyChanged?.Invoke(Instance,new(nameof(Color)));
    }
}

internal static class FilterUsageLog
{
    private sealed class Session(string path,Channel<string> queue) { public string Path=path;public Channel<string> Queue=queue;public long Dropped; }
    private static readonly object gate=new();
    private static Session session;
    private static long nextSample;
    public static Task Completion { get; private set; } = Task.CompletedTask;
    private static readonly JsonSerializerOptions options=new(){NumberHandling=JsonNumberHandling.AllowNamedFloatingPointLiterals};
    public static string DirectoryPath {get;private set;}
    public static void Configure(bool enabled, string root = null)
    {
        lock(gate) {
            if(enabled && session==null) {
                try {
                    DirectoryPath=System.IO.Path.Combine(root ?? @"D:\0SR-plus-work\滤波使用记录",DateTime.Now.ToString("yyyy-MM-dd"),DateTime.Now.ToString("HHmmss")+"-"+Guid.NewGuid().ToString("N")[..6]);
                    Directory.CreateDirectory(DirectoryPath);
                    var stream=new FileStream(System.IO.Path.Combine(DirectoryPath,"filter-events.jsonl"),FileMode.CreateNew,FileAccess.Write,FileShare.Read);
                    var s=new Session(DirectoryPath,Channel.CreateBounded<string>(new BoundedChannelOptions(8192){SingleReader=true,FullMode=BoundedChannelFullMode.Wait}));
                    identities.Clear();session=s;var worker = Task.Run(async()=>{
                        try {using var writer=new StreamWriter(stream);await foreach(var line in s.Queue.Reader.ReadAllAsync()) {await writer.WriteLineAsync(line);await writer.FlushAsync();}
                            await writer.WriteLineAsync(JsonSerializer.Serialize(new{kind="session_end",utc=DateTimeOffset.UtcNow,dropped=s.Dropped}));}
                        catch(Exception ex){FilterRuntime.Status("滤波日志写入失败："+ex.Message,true);}
                    });
                    Completion=Task.WhenAll(Completion,worker);
                    Write("session_start",new{schema=1,build="V2.14",algorithm="adaptive-shrink-r3",timebase="video seconds; requested != queued != scheduled-effective",motorLogs=TestRecording.DirectoryPath});
                } catch(Exception ex){FilterRuntime.Status("滤波日志创建失败："+ex.Message,true);}
            } else if(!enabled && session!=null) {Write("filter_disabled",new{});session.Queue.Writer.TryComplete();session=null;}
        }
    }
    public static void Write(string kind,object data)
    {
        var s=Volatile.Read(ref session);if(s==null)return;
        try {var line=JsonSerializer.Serialize(new{kind,utc=DateTimeOffset.UtcNow,hostTicks=Stopwatch.GetTimestamp(),hostFrequency=Stopwatch.Frequency,data},options);
            if(!s.Queue.Writer.TryWrite(line))Interlocked.Increment(ref s.Dropped);}
        catch(Exception ex){FilterRuntime.Status("滤波日志编码失败："+ex.Message,true);}
    }
    public static bool SampleDue() {var now=Stopwatch.GetTimestamp();var old=Interlocked.Read(ref nextSample);return now>=old && Interlocked.CompareExchange(ref nextSample,now+Stopwatch.Frequency,old)==old;}
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,string> identities=new();
    public static void Identify(string path)
    {
        if(string.IsNullOrWhiteSpace(path))return;
        _=Task.Run(()=>{
            try {var info=new FileInfo(path);if(!info.Exists)return;
                var key=path+"|"+info.Length+"|"+info.LastWriteTimeUtc.Ticks;
                if(identities.ContainsKey(key))return;
                using var f=File.OpenRead(path);var hash=Convert.ToHexString(SHA256.HashData(f));
                if(identities.TryAdd(key,hash))Write("script_identity",new{path,sha256=hash,bytes=info.Length,modifiedUtc=info.LastWriteTimeUtc});}
            catch(Exception ex){Write("script_identity_error",new{path,error=ex.Message});}
        });
    }
}
