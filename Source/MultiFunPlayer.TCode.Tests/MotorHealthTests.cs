using System.Buffers.Binary;
using System.Threading.Channels;
using System.Text.Json;
using MultiFunPlayer.OutputTarget.Tracking;
using MultiFunPlayer.OutputTarget.TCodeBuffered;
namespace MultiFunPlayer.TCode.Tests;
[Collection("Recorder")]
public class MotorHealthTests
{
    static string Fixture => File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"TestData","motor-health.frame"));
    [Fact]
    public void DecodesProductionFirmwareBytesAndUnits()
    {
        Assert.True(MotorHealthPacket.TryDecode(Fixture,out var p,out var error),error);
        Assert.Equal(99u,p.Boot);Assert.Equal(42u,p.Session);Assert.Equal(17u,p.Sequence);
        Assert.Equal(1000000UL,p.TimeMicroseconds);Assert.Equal(9u,p.SkippedQueries);
        Assert.Equal(1,p.Motors[0].Values[0]);Assert.Equal(-2.5,p.Motors[0].Values[1]);
        Assert.Equal(45,p.Motors[0].Values[2]);Assert.Equal(36,p.Motors[0].Values[3]);
        Assert.Equal(24.2,p.Motors[0].Values[4],3);
        Assert.Null(p.Motors[6].Value(3,0));
    }
    [Fact]
    public void RejectsCorruptAndFalselyValidStaleValues()
    {
        Assert.False(MotorHealthPacket.TryDecode(Fixture.Replace("MTR1","MTR2"),out _,out _));
        var bytes=Convert.FromBase64String(Fixture[6..].Trim());
        bytes[32]^=1;
        Assert.False(MotorHealthPacket.TryDecode("!MTR1,"+Convert.ToBase64String(bytes),out _,out _));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(48),101);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(308),TrackingPacket.Crc32(bytes.AsSpan(0,308)));
        Assert.False(MotorHealthPacket.TryDecode("!MTR1,"+Convert.ToBase64String(bytes),out _,out _));
    }
    [Fact]
    public void AgesEachSensorIndependentlyAndClearsOnReconnect()
    {
        Assert.True(MotorHealthPacket.TryDecode(Fixture,out var p,out _));
        Assert.NotNull(p.Motors[0].Value(0,90));Assert.Null(p.Motors[0].Value(0,96));
        Assert.NotNull(p.Motors[0].Value(1,600));Assert.Null(p.Motors[0].Value(1,651));
        Assert.NotNull(p.Motors[0].Value(4,999));Assert.Null(p.Motors[0].Value(4,1001));
        using var m=new TrackingMonitor();
        m.AcceptMotor(p,1000000);
        m.AcceptMotor(p with {Sequence=16,TimeMicroseconds=999999},1000000);
        Assert.Equal(17u,m.MotorHealth.Packet.Sequence);
        m.BeginConnection();Assert.Null(m.MotorHealth);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OptionalSubscriptionAndBrokenSensorPacketNeverConsumeMotionAck(bool supported)
    {
        var rx=Channel.CreateUnbounded<string>();
        using var monitor=new TrackingMonitor();
        var healthRequested=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ops=new System.Collections.Concurrent.ConcurrentQueue<ulong>();
        await using var client=new TCodeBufferedClient((wire,_)=>{
            Assert.True(TCodeBufferedProtocol.TryDecode(wire,out var f));ops.Enqueue(f[0]);
            ulong[] response=f[0]==12 || (f[0]==13 && supported)
                ? [f[0]==12?106UL:107UL,f[1],99,0,0,0,1,f[4],f[5],1]
                : [100,f[1],99,0,0,0,1000000,0,512,512,512,512,512,512,512,0,0,0,0,0,0,0];
            if(f[0]==13)healthRequested.TrySetResult();
            rx.Writer.TryWrite("!MTR1,broken"+Environment.NewLine+Fixture+TCodeBufferedProtocol.Encode(response));
            return Task.CompletedTask;
        },async ct=>await rx.Reader.ReadAsync(ct),tracking:monitor);
        await client.SendAsync(0,CancellationToken.None);
        client.StartTracking();
        await healthRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var ack=await client.SendAsync(0,CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(100UL,ack[0]);Assert.False(client.MayOwnMotion);
        Assert.DoesNotContain(5UL,ops); // sensor failure must not trigger motion STOP
    }
    [Fact]
    public async Task PauseAnnotationUsesMediaPositionNotWallClockAndLogsHealth()
    {
        TestRecording.Start(Path.Combine(Path.GetTempPath(),"mfp-motor-"+Guid.NewGuid().ToString("N")));
        var folder=TestRecording.DirectoryPath;
        try {
            using var monitor=new TrackingMonitor();
            Assert.True(MotorHealthPacket.TryDecode(Fixture,out var p,out _));
            monitor.AcceptMotor(p,1001000);
            TestRecording.Annotate("异响","第一次",123.456,false,@"E:/任意目录/test.funscript");
            await Task.Delay(30);
            TestRecording.Annotate("抖动","第二次",123.456,false,@"E:/任意目录/test.funscript");
        } finally {await TestRecording.StopAsync();}
        var rows=File.ReadAllLines(Path.Combine(folder,"events.jsonl")).Select(x=>JsonDocument.Parse(x).RootElement.Clone()).ToArray();
        var marks=rows.Where(x=>x.GetProperty("kind").GetString()=="annotation").ToArray();
        Assert.Equal(2,marks.Length);
        Assert.All(marks,x=>{var d=x.GetProperty("data");Assert.Equal(123.456,d.GetProperty("mediaSeconds").GetDouble());Assert.True(d.GetProperty("paused").GetBoolean());});
        Assert.Single(rows.Where(x=>x.GetProperty("kind").GetString()=="motor_health"));
    }
}
