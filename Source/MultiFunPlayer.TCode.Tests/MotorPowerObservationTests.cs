using System.Text.Json;
using MultiFunPlayer.OutputTarget.Tracking;
namespace MultiFunPlayer.TCode.Tests;
public class MotorPowerObservationTests
{
    static MotorHealthPacket Packet(uint boot=1,bool fresh=true) => new(boot,1,1,1000,1,200,0,
        Enumerable.Range(0,7).Select(i=>new MotorHealth([0,0,20,20,48],[(ushort)(fresh?0:65535),0,0,0,0],(ushort)(fresh?0:65535),0,8,(byte)(i==0?(fresh?31:30):0))).ToArray());
    static JsonElement M(MotorPowerObservation o,double t) => JsonSerializer.SerializeToElement(o.Snapshot(t)).GetProperty("motors")[0];
    [Fact] public void MissingFeedbackDoesNotMeanPowerOffOrAdvanceDuration()
    {
        var o=new MotorPowerObservation();var utc=DateTimeOffset.UtcNow;
        o.Observe(Packet(),10,utc,0);o.Observe(Packet(),11,utc.AddSeconds(1),0);
        var m=M(o,15);Assert.Equal("feedback_missing_power_unknown",m.GetProperty("state").GetString());Assert.Equal(1,m.GetProperty("observedSpanSeconds").GetDouble());
        var id=m.GetProperty("segmentId").GetString();o.Observe(Packet(),16,utc.AddSeconds(6),0);
        Assert.NotEqual(id,M(o,16).GetProperty("segmentId").GetString());Assert.Equal(0,M(o,16).GetProperty("observedSpanSeconds").GetDouble());
    }
    [Fact] public void CachedVoltageCannotStartPowerObservation()
    {
        var o=new MotorPowerObservation();o.Observe(Packet(fresh:false),10,DateTimeOffset.UtcNow,0);
        Assert.Equal(JsonValueKind.Null,M(o,10).GetProperty("segmentId").ValueKind);
    }
    [Fact] public void ReconnectOrControllerBootStartsUnknownNewSegment()
    {
        var o=new MotorPowerObservation();var utc=DateTimeOffset.UtcNow;o.Observe(Packet(),10,utc,0);var first=M(o,10).GetProperty("segmentId").GetString();
        o.Observe(Packet(2),11,utc.AddSeconds(1),0);Assert.NotEqual(first,M(o,11).GetProperty("segmentId").GetString());
        o.Reset();Assert.Equal(JsonValueKind.Null,M(o,12).GetProperty("segmentId").ValueKind);
    }
}
