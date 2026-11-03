using MultiFunPlayer.Common;
using MultiFunPlayer.OutputTarget.TCodeBuffered;

namespace MultiFunPlayer.TCode.Tests;
public class MotionScriptFilterTests
{
    private static readonly MotionFilterSettings Enabled = new(true) { AlgorithmVersion = 2 };
    private static Keyframe[] Wave(double halfPeriod, double amplitude, int count = 101) =>
        Enumerable.Range(0, count).Select(i => new Keyframe(i * halfPeriod, .5 + (i % 2 == 0 ? -amplitude : amplitude))).ToArray();
    private static double Span(IReadOnlyList<Keyframe> p) => p.Skip(10).Take(p.Count - 20).Max(k => k.Value) - p.Skip(10).Take(p.Count - 20).Min(k => k.Value);

    [Fact] public void SlowStrokeAndDisabledAxisAreExactBypass()
    {
        var slow = Wave(2, .4);
        Assert.Same(slow, MotionScriptFilter.Apply(slow, Enabled));
        var fast = Wave(.02, .2);
        Assert.Same(fast, MotionScriptFilter.Apply(fast, new()));
        Assert.Same(fast, MotionScriptFilter.Apply(fast, Enabled with { L0 = new(false, 80) }));
        Assert.Same(fast, MotionScriptFilter.Apply(fast, Enabled with { L0 = new(true, 0) }));
    }
    [Theory] [InlineData(.02, .03)] [InlineData(.15, .4)]
    public void ShortFastAndLongModeratelyFastStrokesShrinkWithoutRetiming(double period, double amplitude)
    {
        var source = Wave(period, amplitude); var copy = source.ToArray();
        var output = MotionScriptFilter.Apply(source, Enabled);
        Assert.True(Span(output) < Span(source) * .7);
        Assert.Equal(source.Select(p => p.Position), output.Select(p => p.Position));
        Assert.Equal(source[0], output[0]); Assert.Equal(source[^1], output[^1]);
        Assert.Equal(copy, source); AssertContract(source, output);
    }
    [Fact] public void AxesAndStrengthAreIndependent()
    {
        var source = Wave(.1, .4);
        var settings = Enabled with { L0 = new(true, 25), R2 = new(true, 90), R1 = new(false, 90) };
        Assert.True(Span(MotionScriptFilter.Apply(source, settings, axis: "R2")) < Span(MotionScriptFilter.Apply(source, settings)));
        Assert.Same(source, MotionScriptFilter.Apply(source, settings, axis: "R1"));
    }
    [Fact] public void RateAndScaleAffectSpeedTriggerButScaleDoesNotHideRapidReversals()
    {
        var source = Wave(.5, .5);
        Assert.Same(source, MotionScriptFilter.Apply(source, Enabled));
        Assert.NotSame(source, MotionScriptFilter.Apply(source, Enabled, 1.25));
        Assert.Same(source, MotionScriptFilter.Apply(source, Enabled, 1.25, .25));
        Assert.NotSame(source, MotionScriptFilter.Apply(Wave(.02, .01), Enabled, 1, .1));
    }
    [Fact] public void IsolatedFastSectionDoesNotLowerDistantSlowSections()
    {
        var source = new List<Keyframe> { new(0,.2), new(2,.8), new(4,.2) };
        source.AddRange(Wave(.04,.1,101).Select(p=>p with {Position=p.Position+6}));
        source.AddRange(new Keyframe[] {new(12,.2),new(14,.8),new(16,.2),new(18,.8)});
        var output=MotionScriptFilter.Apply(source,Enabled);
        Assert.Equal(source[1],output[1]); Assert.Equal(source[^2],output[^2]); AssertContract(source,output);
        Assert.True(output.Skip(20).Take(40).Max(p=>p.Value)-output.Skip(20).Take(40).Min(p=>p.Value)<.15);
    }
    [Fact] public void HoldsRemainFlatAndDenseCollinearSubdivisionsDoNotChangeResult()
    {
        var source=Wave(.05,.2,21);
        var dense=new List<Keyframe>();
        for(var i=0;i<source.Length-1;i++) for(var j=0;j<5;j++) dense.Add(new(source[i].Position+j*.01,source[i].Value+(source[i+1].Value-source[i].Value)*j/5));
        dense.Add(source[^1]);
        var a=MotionScriptFilter.Apply(source,Enabled); var b=MotionScriptFilter.Apply(dense,Enabled);
        for(var i=0;i<a.Count;i++) Assert.Equal(a[i].Value,b[i*5].Value,8);
        Keyframe[] held=[new(0,.5),new(.03,.8),new(.06,.2),new(.09,.8),new(.2,.5),new(10,.5),new(12,.8)];
        var result=MotionScriptFilter.Apply(held,Enabled);
        Assert.Equal(result[4].Value,result[5].Value,8);AssertContract(held,result);
    }
    [Fact] public void RandomMixedScriptsNeverCreateFasterEdgesOrReverseDirections()
    {
        var random=new Random(42);
        for(var run=0;run<30;run++) {
            double t=0;var source=new List<Keyframe>();
            for(var i=0;i<500;i++){t+=.001+random.NextDouble()*.5;source.Add(new(t,random.NextDouble()));}
            AssertContract(source,MotionScriptFilter.Apply(source,Enabled));
        }
    }
    [Fact] public void AllSixAxesProduceUsableBufferedPlans()
    {
        var axes=MotionScriptFilter.Axes.Select(name=>new BufferedScriptAxis(name,MotionScriptFilter.Apply(Wave(.05,.2),Enabled,axis:name),0,1,false,.5)).ToArray();
        var plan=TCodeBufferedPlan.Create(new(false,0,1,1,axes),1000000);
        Assert.Equal(6,plan.Count(a=>a.Count>0));
        Assert.All(plan,a=>Assert.True(a.Zip(a.Skip(1)).All(p=>p.Second.Microseconds>p.First.Microseconds)));
    }
    [Theory] [InlineData(double.NaN)] [InlineData(-1)] [InlineData(101)]
    public void InvalidStrengthIsRejected(double strength)=>Assert.Throws<ArgumentOutOfRangeException>(()=>MotionScriptFilter.Apply(Wave(.1,.2),Enabled with{R2=new(true,strength)}));
    [Fact] public void StrengthOneCannotHalveEvenExtremeStrokes()
    {
        var source=Wave(.001,.45);
        var result=MotionScriptFilter.Apply(source,Enabled with {L0=new(true,1)});
        for(var i=1;i<source.Length;i++)
            Assert.True(Math.Abs(result[i].Value-result[i-1].Value)>=.95*Math.Abs(source[i].Value-source[i-1].Value)-1e-9);
        AssertContract(source,result);
    }
    [Fact] public void ShortHeldTurnsAreDetectedAtStrengthTwenty()
    {
        var source=new List<Keyframe>();
        for(var i=0;i<100;i++) {var v=i%2==0?.48:.52;source.Add(new(i*.1,v));source.Add(new(i*.1+.01,v));}
        var result=MotionScriptFilter.Apply(source,Enabled with {L0=new(true,20)});
        Assert.True(Span(result)<Span(source)*.7);AssertContract(source,result);
    }
    [Fact] public void ModerateRepeatedSmallStrokesAreDetectedWithoutNeedingHighLinearSpeed()
    {
        var source=Wave(.2,.02);
        var result=MotionScriptFilter.Apply(source,Enabled with {L0=new(true,20)});
        Assert.True(Span(result)<Span(source)*.95);AssertContract(source,result);
    }
    private static void AssertContract(IReadOnlyList<Keyframe> source,IReadOnlyList<Keyframe> output)
    {
        Assert.Equal(source.Count,output.Count);
        for(var i=0;i<source.Count;i++){
            Assert.Equal(source[i].Position,output[i].Position);Assert.InRange(output[i].Value,0,1);
            if(i==0)continue;var dx=source[i].Value-source[i-1].Value;var dy=output[i].Value-output[i-1].Value;
            Assert.True(Math.Abs(dy)<=Math.Abs(dx)+1e-8);Assert.True(dx*dy>=-1e-12);
            if(Math.Abs(dx)>1e-6)Assert.True(dx*dy>0);
        }
    }
}
