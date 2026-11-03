using MultiFunPlayer.OutputTarget.Tracking;
namespace MultiFunPlayer.TCode.Tests;
public class ResidualLoadTests
{
    private static TrackingPacket Pose(ulong t, double q = 0, uint boot = 1) => new(boot,1,(uint)(t/100000),t,63,
        TrackingFlags.Running,63,63,63,new double[6],Enumerable.Repeat(q,7).ToArray(),Enumerable.Repeat(q,7).ToArray(),new ushort[7],new ushort[7],100,0,100);
    private static MotorHealthPacket Health(ulong t, double iq = 1, ushort age = 0) => new(1,1,(uint)(t/100000),t,63,100,0,
        Enumerable.Range(0,7).Select(_ => new MotorHealth([0,iq,20,20,48],[0,age,0,0,0],0,0,8,31)).ToArray());
    private static (ResidualLoadObserver O, ulong T) Baseline()
    {
        var o = new ResidualLoadObserver(); o.BeginBaseline();
        ulong t=100000;
        for (;t<=5000000;t+=100000) { o.AddPose(Pose(t)); o.Observe(Health(t),0); }
        Assert.Equal(1,o.Latest.BaselineCount);
        return (o,t);
    }
    [Fact] public void BaselineThenPersistentResidualDoesNotClaimMass()
    {
        var (o,t)=Baseline();
        for(var i=0;i<4;i++,t+=100000) { o.AddPose(Pose(t)); o.Observe(Health(t,2),0); }
        Assert.True(o.Latest.SustainedChange);
        Assert.All(o.Latest.ResidualA,x=>Assert.Equal(1,x,6));
    }
    [Fact] public void CachedCurrentCannotCompleteBaseline()
    {
        var o=new ResidualLoadObserver(); o.BeginBaseline();
        for(ulong t=100000;t<=2000000;t+=100000) o.AddPose(Pose(t));
        o.Observe(Health(2000000),0);
        for(ulong t=2100000;t<=2600000;t+=100000) { o.AddPose(Pose(t)); o.Observe(Health(t,1,(ushort)((t-2000000)/1000)),0); }
        Assert.Equal(0,o.Latest.BaselineCount); Assert.Equal(1,o.Latest.CaptureCount);
    }
    [Fact] public void MotionOrDelayedDataRemovesEstimate()
    {
        var(o,t)=Baseline(); o.AddPose(Pose(t,.1));
        Assert.Null(o.Observe(Health(t),0).ResidualA);
        Assert.Null(o.Observe(Health(t),300).ResidualA);
    }
    [Fact] public void RebootClearsBaseline()
    {
        var(o,t)=Baseline(); o.AddPose(Pose(t,0,2));
        Assert.Equal(0,o.Observe(Health(t),0).BaselineCount);
    }
    [Fact] public void UnknownPoseIsNotZeroLoad()
    {
        var(o,t)=Baseline();
        for(var i=0;i<20;i++,t+=100000) {o.AddPose(Pose(t,.5));o.Observe(Health(t),0);}
        Assert.Contains("未采集",o.Latest.State); Assert.Null(o.Latest.ResidualA);
    }
}
