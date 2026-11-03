using MultiFunPlayer.Common;
using MultiFunPlayer.OutputTarget.TCodeBuffered;
namespace MultiFunPlayer.TCode.Tests;
public class PlateauTimingTests
{
    [Fact] public void ArrivalAndDepartureRemainAtTheirOriginalTimes()
    {
        Keyframe[] keys=[new(0,.2),new(1,.8),new(2,.8),new(3,.8),new(4,.3)];
        var turns=TCodeBufferedPlan.TurningPoints(keys);
        Assert.Contains(keys[1],turns); Assert.Contains(keys[3],turns);
        Assert.Equal(4,turns.Count);
        var nodes=TCodeBufferedPlan.Create(ProtocolAndPlanTests.Snapshot(keys),0)[0];
        Assert.Equal(nodes.Single(n=>n.Microseconds==1000000).Position,nodes.Single(n=>n.Microseconds==3000000).Position);
    }
    [Fact] public void SameDirectionPauseAndFinalHoldSurvive()
    {
        Keyframe[] keys=[new(0,.1),new(1,.4),new(2,.4),new(3,.8),new(4,.8)];
        Assert.Equal(keys,TCodeBufferedPlan.TurningPoints(keys));
    }
    [Fact] public void Reported161SecondArrivalIsNotDelayed()
    {
        Keyframe[] keys=[new(160.821,1),new(161.194,.7),new(161.828,1),new(162.982,1),new(164.274,1),new(164.931,.8)];
        var turns=TCodeBufferedPlan.TurningPoints(keys);
        Assert.Contains(keys[2],turns);Assert.Contains(keys[4],turns);
        Assert.DoesNotContain(keys[3],turns);
    }
}
