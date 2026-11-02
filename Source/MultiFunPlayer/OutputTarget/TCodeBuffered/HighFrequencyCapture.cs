using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using MultiFunPlayer.OutputTarget.Tracking;

namespace MultiFunPlayer.OutputTarget.TCodeBuffered;

// Optional diagnostics: failures never propagate to motion playback. Uses only
// the connection's existing writer/reader; never opens or resets a serial port.
internal sealed class HighFrequencyCapture : IAsyncDisposable
{
    private readonly Func<string,CancellationToken,Task> write;
    private readonly Func<bool> paused;
    private readonly Func<ulong> boot;
    private readonly Action<string> log;
    private readonly Func<bool> recording;
    private readonly Func<bool> enabled;
    private readonly Func<string> recordingDirectory;
    private readonly CancellationTokenSource cancel = new();
    private readonly Channel<string> replies = Channel.CreateBounded<string>(8);
    private readonly Task worker;
    private ulong request;
    private bool? compressed;
    private FileStream exportFile;
    private int exportKind=-1;
    private long checkpoint;
    private string name, directory;
    private ulong captureBoot;
    private bool seenPlayback, stopped, suspended;
    private long[] sizes, offsets = new long[2];
    public HighFrequencyCapture(Func<string,CancellationToken,Task> write, Func<bool> paused,
        Func<ulong> boot, Action<string> log, Func<bool> recording=null, Func<string> recordingDirectory=null, Func<bool> enabled=null)
    { this.write=write; this.paused=paused; this.boot=boot; this.log=log;
      this.enabled=enabled ?? (()=>true);
      this.recording=recording ?? (()=>TestRecording.Active);
      this.recordingDirectory=recordingDirectory ?? (()=>TestRecording.DirectoryPath);
      worker=Task.Run(Run); }
    internal bool IsExporting => stopped && name!=null && sizes!=null && paused();
    public void Receive(string line) => replies.Writer.TryWrite(line.Trim());
    private async Task<string[]> Command(string command, CancellationToken token)
    {
        var id=++request;
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(2000);
        await write($"DHF {id} {command}\n",timeout.Token);
        while(true)
        {
            var fields=(await replies.Reader.ReadAsync(timeout.Token)).Split(',');
            if(fields.Length<3 || fields[1]!=id.ToString())continue;
            if(fields[2]=="ERR")throw new IOException(string.Join(',',fields));
            return fields;
        }
    }
    private async Task<List<string[]>> ReadBatch(int kind, long offset, CancellationToken token)
    {
        if(compressed!=false) {
            try { var r=await Command($"readz {name} {kind} {offset}",token); compressed=true;return [r]; }
            catch(IOException e) when(e.Message.EndsWith(",ERR,verb",StringComparison.Ordinal)) {compressed=false;}
        }
        // Bounded legacy pipeline: at most four blocks, stop submitting on resume.
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(3000);
        var ids=new List<ulong>();
        for(int i=0;i<4 && offset+i*128<sizes[kind] && paused();i++) {
            var id=++request;ids.Add(id);
            await write($"DHF {id} read {name} {kind} {offset+i*128}\n",timeout.Token);
        }
        var found=new Dictionary<ulong,string[]>();
        while(found.Count<ids.Count) {
            var f=(await replies.Reader.ReadAsync(timeout.Token)).Split(',');
            if(f.Length<3 || !ulong.TryParse(f[1],out var id) || !ids.Contains(id))continue;
            if(f[2]=="ERR")throw new IOException(string.Join(',',f));found[id]=f;
        }
        return ids.Select(id=>found[id]).ToList();
    }
    private void Checkpoint()
    {
        exportFile?.Flush(true); checkpoint=Environment.TickCount64;Save("exporting");
    }
    internal static byte[] DecodeDelta(byte[] packed, int length, long offset, int stride, Stream file)
    {
        if(length<1 || length>1024 || offset<0)throw new IOException("Invalid compressed size");
        var delta=new byte[length];int dst=0,src=0;
        while(src<packed.Length) {
            var tag=packed[src++];int count=(tag&127)+1;
            if(dst+count>length)throw new IOException("Compressed overrun");
            if((tag&128)==0) {
                if(src+count>packed.Length)throw new IOException("Truncated compressed block");
                Array.Copy(packed,src,delta,dst,count);src+=count;
            }
            dst+=count;
        }
        if(dst!=length)throw new IOException("Compressed underrun");
        var baseStart=Math.Max(0,offset-stride);
        var baseEnd=Math.Min(offset,Math.Max(0,offset+length-stride));
        var history=new byte[(int)(baseEnd-baseStart)];
        if(history.Length>0) {file.Position=baseStart;file.ReadExactly(history);}
        for(int i=0;i<length;i++) {
            long previous=offset+i-stride;if(previous<0)continue;
            delta[i]^=previous>=offset?delta[(int)(previous-offset)]:history[(int)(previous-baseStart)];
        }
        return delta;
    }
    private void Save(string status, string detail=null)
    {
        TestRecording.HighFrequencyStatus = status switch {
            "arming" => "高频采集准备中", "recording" => "高频采集中（缓存满后停止）",
            "exporting" => $"高频导出 {(sizes?.Sum()>0 ? offsets.Sum()*100d/sizes.Sum() : 0):F1}%",
            "export_suspended" => "高频导出暂停：此段暂无新高频数据",
            "complete" => "高频记录已保存", "verified" => "高频记录校验完成",
            _ => "高频记录未完成："+detail };
        var json=JsonSerializer.Serialize(new {schema=1, traceVersion=5, motionRecordBytes=389,
            canRecordBytes=808, captureName=name, boot=captureBoot, status, detail,
            bufferMayHaveFilled=sizes!=null && (sizes[0]>=4194304/389*389 || sizes[1]>=8388608/808*808),
            sizes, offsets, updatedUtc=DateTimeOffset.UtcNow,
            timebase="CAN tick_time_us=P4 boot clock; use parent events CLOCK mapping per boot/session",
            source="passive control-cycle ring; current/temperature/voltage are cached, not 200Hz measurements"},new JsonSerializerOptions{WriteIndented=true});
        File.WriteAllText(Path.Combine(directory,"capture.json.tmp"),json);
        File.Move(Path.Combine(directory,"capture.json.tmp"),Path.Combine(directory,"capture.json"),true);
    }
    private async Task Run()
    {
        var token=cancel.Token;
        try
        {
            while(!token.IsCancellationRequested)
            {
                if(name==null)
                {
                    if(!enabled() || !recording() || !paused()) { await Task.Delay(200,token);continue; }
                    TestRecording.HighFrequencyBusy=true;
                    name="hf_"+Guid.NewGuid().ToString("N");captureBoot=boot();
                    directory=Path.Combine(recordingDirectory(),"HighFrequency",name);
                    Directory.CreateDirectory(directory);Save("arming");
                    await Command($"start {name}",token);
                    Save("recording");log?.Invoke("HF recording armed "+name);
                }
                if(boot()!=captureBoot)throw new IOException("Device boot changed; retained capture cannot be assumed valid");
                if(!stopped)
                {
                    if(!paused()) {if(!seenPlayback) {await Command($"run {name}",token);seenPlayback=true;Save("recording");} await Task.Delay(200,token);continue;}
                    if(!seenPlayback && recording()) {await Task.Delay(200,token);continue;}
                    var info=await Command($"stop {name}",token);
                    if(info.Length!=8 || info[2]!="OK" || info[3]!="stop")throw new IOException("Invalid stop response");
                    sizes=[long.Parse(info[4]),long.Parse(info[5])];
                    if(sizes[0]<0 || sizes[1]<0 || sizes[0]%389!=0 || sizes[1]%808!=0 || sizes[0]>4194304 || sizes[1]>8388608 || info[7]!="0")throw new IOException("Invalid/overwritten trace sizes");
                    stopped=true;Save("exporting",$"skipped={info[6]}");
                }
                if(!paused()) {if(!suspended){Checkpoint();Save("export_suspended");suspended=true;} await Task.Delay(100,token);continue;}
                suspended=false;
                var kind=offsets[0]<sizes[0]?0:1;
                if(offsets[kind]<sizes[kind])
                {
                    if(exportKind!=kind) {
                        if(exportFile!=null){Checkpoint();exportFile.Dispose();}
                        exportFile=new FileStream(Path.Combine(directory,kind==0?"motion.bin":"can.bin"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.Read,65536);
                        exportKind=kind;
                    }
                    List<string[]> batch=null;
                    for(int attempt=0;attempt<3;attempt++) {
                        try {batch=await ReadBatch(kind,offsets[kind],token);break;}
                        catch(OperationCanceledException) when(!token.IsCancellationRequested && attempt<2) {
                            log?.Invoke("HF block timeout; retry same offset");
                            while(!paused())await Task.Delay(100,token);
                            if(boot()!=captureBoot)throw new IOException("Device restarted during export");
                        }
                    }
                    foreach(var reply in batch) {
                        var offset=offsets[kind];bool zipped=reply.Length==8 && reply[2]=="ZDATA";
                        if((!zipped && (reply.Length!=7 || reply[2]!="DATA")) || reply[3]!=kind.ToString() || reply[4]!=offset.ToString())throw new IOException("Invalid export reply");
                        var bytes=zipped?DecodeDelta(Convert.FromHexString(reply[7]),int.Parse(reply[6]),offset,kind==0?389:808,exportFile):Convert.FromHexString(reply[6]);
                        if(bytes.Length!=Math.Min(zipped?1024:128,sizes[kind]-offset) || Crc(bytes)!=ushort.Parse(reply[5]))throw new IOException("High frequency CRC/size mismatch");
                        exportFile.Position=offset;exportFile.Write(bytes);offsets[kind]+=bytes.Length;
                    }
                    // Durable checkpoint once a second, not once per 128 bytes.
                    if(Environment.TickCount64-checkpoint>=1000)Checkpoint();
                    continue;
                }
                if(exportFile!=null){Checkpoint();exportFile.Dispose();exportFile=null;exportKind=-1;}
                var hashes=new Dictionary<string,string>();
                foreach(var (fileName,index) in new[]{("motion.bin",0),("can.bin",1)})
                {
                    var path=Path.Combine(directory,fileName);
                    if(sizes[index]==0 && !File.Exists(path))File.WriteAllBytes(path,[]);
                    using var file=File.OpenRead(path);
                    if(file.Length!=sizes[index])throw new IOException("Saved size mismatch");
                    hashes[fileName]=Convert.ToHexString(SHA256.HashData(file));
                }
                File.WriteAllText(Path.Combine(directory,"sha256.json"),JsonSerializer.Serialize(hashes));
                Save("verified");
                // Release only this uniquely owned capture, after durable local verification.
                await Command($"release {name}",token);Save("complete");
                log?.Invoke("HF saved "+directory);
                TestRecording.HighFrequencyBusy=false;
                name=null;seenPlayback=stopped=false;sizes=null;offsets=new long[2];
            }
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested)
        { exportFile?.Flush(true); if(directory!=null && name!=null) Save("interrupted","Device copy retained; do not reboot before recovery"); }
        catch(Exception error)
        {
            exportFile?.Flush(true);
            log?.Invoke("HF diagnostic unavailable; motion unchanged: "+error.Message);
            if(directory!=null)try {Save("incomplete",error.Message);}catch{}
        }
        finally {exportFile?.Dispose();TestRecording.HighFrequencyBusy=false;}
    }
    internal static ushort Crc(ReadOnlySpan<byte> bytes)
    {
        ushort crc=0xffff;
        foreach(var b in bytes){crc^=(ushort)(b<<8);for(var i=0;i<8;i++)crc=(ushort)((crc&0x8000)!=0?(crc<<1)^0x1021:crc<<1);}
        return crc;
    }
    public async ValueTask DisposeAsync(){cancel.Cancel();try{await worker;}catch{}cancel.Dispose();}
}
