using MultiFunPlayer.Common;
using MultiFunPlayer.OutputTarget.TCodeBuffered;
namespace MultiFunPlayer.TCode.Tests;
public class LiveFilterSpliceTests
{
    static List<BufferedNode>[] Wave(int amplitude)=>Enumerable.Range(0,6).Select(axis=>Enumerable.Range(0,40).Select(i=>new BufferedNode(axis,(ulong)i*500000,(ulong)(5000+(i%2==0?-amplitude:amplitude)))).ToList()).ToArray();
    [Fact] public void AcceptedPrefixIsImmutableAndFutureTailsMeetEnvelope()
    {
        var old=Wave(1000);var target=Wave(500);var copy=old.Select(a=>a.ToArray()).ToArray();
        int[] sent=[8,9,8,9,8,9];var limits=new BufferedSessionLimits(new(100,10000,1000000),new(100,10000,1000000));
        var r=BufferedFilterSplice.Create(old,target,sent,new bool[6],limits,2000000);
        Assert.True(r.Start>=4000000);Assert.True(r.End>r.Start);
        for(var axis=0;axis<6;axis++) {
            Assert.Equal(copy[axis],old[axis]);
            Assert.Equal(old[axis].Take(sent[axis]),r.Nodes[axis].Take(sent[axis]));
            Assert.Equal(target[axis][^1],r.Nodes[axis][^1]);
            Assert.All(r.Nodes[axis].Zip(r.Nodes[axis].Skip(1)),p=>{
                var dt=(p.Second.Microseconds-p.First.Microseconds)/1e6;
                Assert.True(dt>=.001);Assert.True(Math.Abs((double)p.Second.Position-p.First.Position)/10000<=limits.ForAxis(axis).AllowedDistance(dt));
            });
        }
    }
    [Fact] public void SealedOrInfeasibleTailsAreRejectedWithoutEditingOldPlan()
    {
        var old=Wave(100);var target=Wave(4000);var copy=old.Select(a=>a.ToArray()).ToArray();
        var limits=new BufferedSessionLimits(new(.01,.01,.01),new(.01,.01,.01));
        Assert.Throws<System.IO.InvalidDataException>(()=>BufferedFilterSplice.Create(old,target,new[]{8,8,8,8,8,8},new bool[6],limits,1000000));
        for(var i=0;i<6;i++)Assert.Equal(copy[i],old[i]);
        Assert.Throws<System.IO.InvalidDataException>(()=>BufferedFilterSplice.Create(old,target,new[]{8,8,8,8,8,8},new[]{true,false,false,false,false,false},limits,1000000));
    }
    [Fact] public void SessionEnvelopeAllowsRestoringRawScriptWithoutMidSessionLimitChanges()
    {
        Keyframe[] raw=[new(0,.2),new(1,.8),new(2,.2),new(3,.8)];
        Keyframe[] filtered=[new(0,.4),new(1,.6),new(2,.4),new(3,.6)];
        var pose=new BufferedDevicePose(new[]{.5,.5,.5,.5,.5,.5});
        var original=new BufferedPlaybackSnapshot(false,0,1,1,new[]{new BufferedScriptAxis("L0",raw,0,1,false,.5)});
        var adaptive=original with {Axes=new[]{new BufferedScriptAxis("L0",filtered,0,1,false,.5,OriginalKeyframes:raw)}};
        Assert.Equal(TCodeBufferedPlan.CreateSession(original,pose).Limits,TCodeBufferedPlan.CreateSession(adaptive,pose).Limits);
    }
    [Fact] public void FilterOnlyRevisionDoesNotHideRealTimelineOrRangeChanges()
    {
        Keyframe[] raw=[new(0,.2),new(1,.8)];Keyframe[] filtered=[new(0,.3),new(1,.7)];
        var a=new BufferedScriptAxis("L0",raw,0,1,false,.5,OriginalKeyframes:raw);
        var first=new BufferedPlaybackSnapshot(true,0,1,1,new[]{a},1);
        var second=first with{Axes=new[]{a with{Keyframes=filtered}},FilterRevision=2};
        Assert.True(first.HasSameTimeline(second));
        Assert.False(first.HasSameTimeline(second with{Axes=new[]{a with{Maximum=.5}}}));
        Assert.False(first.HasSameTimeline(second with{Revision=2}));
        Assert.False(first.HasSameTimeline(second with{Axes=new[]{a with{OriginalKeyframes=raw.ToArray()}}}));
    }
}
