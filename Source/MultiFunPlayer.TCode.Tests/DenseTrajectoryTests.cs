using MultiFunPlayer.Common;
using MultiFunPlayer.OutputTarget.TCodeBuffered;
using System.Text.Json;
namespace MultiFunPlayer.TCode.Tests;
public class DenseTrajectoryTests
{
    [Fact] public void NormalTurnsRemainUnchanged()
    {
        var turns = new List<Keyframe> {new(0,.2),new(.058,.8),new(.116,.2)};
        Assert.Equal(turns,TCodeBufferedPlan.NormalizeSpacing(turns,1));
    }
    [Fact] public void DenseTurnsKeepClockAndFinalValue()
    {
        var turns = new List<Keyframe> {new(0,.2),new(.1,.8),new(.101,.2),new(.15,.8),new(.151,.3)};
        var result = TCodeBufferedPlan.NormalizeSpacing(turns,1);
        Assert.Equal(turns[0],result[0]);Assert.Equal(turns[^1],result[^1]);
        Assert.All(result.Zip(result.Skip(1)),p=>Assert.True(p.Second.Position-p.First.Position>=.01-1e-10));
        Assert.Equal(5,turns.Count);
    }
    [Fact] public void ActualDenseInputPlansAtReportedSeekPositionsAndSpeedChanges()
    {
        var actions=JsonSerializer.Deserialize<double[][]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"TestData/dense-keyframes.json")));
        var frames=actions.Select(a=>new Keyframe(a[0]/1000,a[1]/100)).ToArray();
        foreach(var speed in new[]{.5,1,2}) foreach(var position in new[]{0,88.516,157.12,487.145,1115.8705})
        {
            var source=ProtocolAndPlanTests.Snapshot(frames) with { Position=position,Speed=speed };
            source=source with {Axes=source.Axes.Select(a=>a with {Scale=.99,Maximum=.7822,OriginalKeyframes=frames}).ToArray()};
            var plan=TCodeBufferedPlan.CreateSession(source,new(Enumerable.Repeat(.5,6).ToArray()));
            Assert.All(plan.Limits.Encode(),x=>Assert.InRange(x,1UL,TCodeBufferedProtocol.MaxInteger));
            var time=0UL;double last=.5;
            foreach(var n in plan.Nodes[0]) {
                Assert.InRange(n.Microseconds-time,1000UL,60000000UL);
                Assert.True(Math.Abs(n.Position/10000d-last)<=plan.Limits.Translation.AllowedDistance((n.Microseconds-time)/1e6));
                time=n.Microseconds;last=n.Position/10000d;
            }
        }
    }
}
