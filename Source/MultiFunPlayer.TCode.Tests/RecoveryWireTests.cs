using MultiFunPlayer.OutputTarget;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;

public class RecoveryWireTests
{
    [Fact]
    public async Task UsesExistingSessionWithFramedRepliesAndNoResetOrMotion()
    {
        var session=new SerialPresetSession();
        var input=new ConcurrentQueue<byte[]>();
        var commands=new List<string>();
        using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var running=session.RunAsync(wire=>{
            var split=wire.Trim().Split(' ',2);
            var id=uint.Parse(split[0][1..]);
            var cmd=split[1];commands.Add(cmd);
            var json=cmd switch {
                "hello"=>"{\"proto\":1,\"device\":\"sr6-p4\"}",
                "sh recover"=>"{\"text\":\"OK recovery requested\"}",
                "sh recover status"=>"{\"text\":\"DONE already healthy; output unchanged\"}",
                _=>throw new Exception("Unexpected command: "+cmd)
            };
            var bytes=Frame(id,json);
            // Simulate chunks arriving independently after unrelated shell text.
            input.Enqueue(Encoding.ASCII.GetBytes("sr6> noise"));
            input.Enqueue(bytes[..7]);input.Enqueue(bytes[7..]);
        },()=>input.TryDequeue(out var b)?b:[],cancel.Token);
        try {
            var result=await MotorFaultRecovery.RunAsync(async c=>(string)(await session.ExecuteAsync("sh "+c,20000))["text"],_=>{});
            Assert.Contains("设备正常",result);
            Assert.Equal(new[]{"hello","sh recover","sh recover status"},commands);
        } finally {session.Close();await running;}
        Assert.True(session.Finished.IsCompletedSuccessfully);
    }
    private static byte[] Frame(uint id,string json)
    {
        var payload=Encoding.UTF8.GetBytes(json);var raw=new byte[payload.Length+18];
        "CTWA"u8.CopyTo(raw);raw[4]=1;
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(8),1);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(10),id);
        payload.CopyTo(raw,14);
        uint crc=0xffffffff;
        foreach(var v in raw.AsSpan(0,raw.Length-4)){crc^=v;for(int n=0;n<8;n++)crc=(crc>>1)^((crc&1)!=0?0xedb88320u:0);}
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(raw.Length-4),~crc);
        var output=new List<byte>{0,0};int start=1;byte code=1;
        foreach(var v in raw){if(v==0){output[start]=code;start=output.Count;output.Add(0);code=1;}else{output.Add(v);if(++code==255){output[start]=code;start=output.Count;output.Add(0);code=1;}}}
        output[start]=code;output.Add(0);return output.ToArray();
    }
}
