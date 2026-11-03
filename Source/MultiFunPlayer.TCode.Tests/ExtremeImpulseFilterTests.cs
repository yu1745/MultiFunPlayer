using MultiFunPlayer.Common;
using MultiFunPlayer.OutputTarget.TCodeBuffered;
using System.Text.Json;
namespace MultiFunPlayer.TCode.Tests;
public class ExtremeImpulseFilterTests
{
    static MotionFilterSettings Settings(double s=50)=>new(true) {
        L0=new(true,s),L1=new(true,s),L2=new(true,s),R0=new(true,s),R1=new(true,s),R2=new(true,s)};
    [Fact] public void SmoothAndSmallFastMotionIsNotFilteredJustForFrequency() {
        Keyframe[] slow=[new(0,.2),new(1,.8),new(2,.2)];
        Keyframe[] small=[new(0,.5),new(.1,.501),new(.2,.5),new(.3,.501)];
        Assert.Same(slow,MotionScriptFilter.Apply(slow,Settings()));
        Assert.Same(small,MotionScriptFilter.Apply(small,Settings()));
    }
    [Fact] public void IsolatedSpikeShrinksWithoutMovingTimeOrChangingNormalRegion() {
        Keyframe[] x=[new(0,.2),new(1,.4),new(2,.4),new(2.016,.7),new(2.032,.4),new(3,.4),new(4,.8)];
        var y=MotionScriptFilter.Apply(x,Settings());
        Assert.Equal(x.Select(p=>p.Position),y.Select(p=>p.Position));
        Assert.True(y[3].Value-y[2].Value<.01);
        Assert.Equal(x[0],y[0]);Assert.Equal(x[^1],y[^1]);
        for(int i=1;i<x.Length;i++) {
            var a=x[i].Value-x[i-1].Value;var b=y[i].Value-y[i-1].Value;
            Assert.InRange(Math.Abs(b),0,Math.Abs(a)+1e-8);
            Assert.True(a*b>=0);if(a==0)Assert.True(Math.Abs(b)<1e-8);
        }
    }
    [Theory] [InlineData("L0")] [InlineData("L1")] [InlineData("L2")]
    [InlineData("R0")] [InlineData("R1")] [InlineData("R2")]
    public void SixAxesKeepTurnsBelowOldOnePercentHysteresis(string axis) {
        Keyframe[] x=[new(0,.4),new(.016,.7),new(.032,.4),new(.048,.7),new(.064,.4)];
        var y=MotionScriptFilter.Apply(x,Settings(),axis:axis);
        Assert.Equal(x.Length,TCodeBufferedPlan.TurningPoints(y,1e-10).Count);
        Assert.True(y[1].Value-y[0].Value<.01);
        Assert.Same(x,MotionScriptFilter.Apply(x,new(false),axis:axis));
    }
    [Fact] public void StrongerSettingContractsMoreButStrengthOneIsGentle() {
        Keyframe[] x=[new(0,.4),new(.016,.7),new(.032,.4)];
        double Stroke(double s){var y=MotionScriptFilter.Apply(x,Settings(s));return y[1].Value-y[0].Value;}
        Assert.True(Stroke(80)<Stroke(20));Assert.True(Stroke(1)>=.3*.95-1e-8);
    }
    [Fact] public void SingleExtremeStrokeAndRandomChainsRemainBounded() {
        Keyframe[] pair=[new(0,.1),new(.02,.9)];
        var limited=MotionScriptFilter.Apply(pair,Settings());
        Assert.True(limited[1].Value-limited[0].Value<.01);
        var rng=new Random(27);
        for(int test=0;test<20;test++) {
            double t=0;var x=Enumerable.Range(0,300).Select(i=>new Keyframe(t+=.01+rng.NextDouble(),rng.NextDouble())).ToArray();
            var y=MotionScriptFilter.Apply(x,Settings());
            for(int i=1;i<x.Length;i++) {
                var d=x[i].Value-x[i-1].Value;var e=y[i].Value-y[i-1].Value;
                Assert.True(d*e>=-1e-9);Assert.True(Math.Abs(e)<=Math.Abs(d)+1e-8);
                Assert.InRange(y[i].Value,0,1);Assert.Equal(x[i].Position,y[i].Position);
            }
        }
    }
    [Fact] public void HistoricalMultiAxisScriptsRetainTimesAndDirections() {
        var root=Environment.GetEnvironmentVariable("CTW_FILTER_MULTI");if(string.IsNullOrEmpty(root))return;
        foreach(var name in new[]{"2·butt dance.funscript","2·butt dance.twist.funscript","2·butt dance.roll.funscript","2·butt dance.pitch.funscript"}) {
            var raw=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,name))).RootElement.GetProperty("actions").EnumerateArray()
                .Select(p=>new Keyframe(p.GetProperty("at").GetDouble()/1000,p.GetProperty("pos").GetDouble()/100)).ToArray();
            var y=MotionScriptFilter.Apply(raw,Settings());
            for(int i=1;i<raw.Length;i++) {
                var a=raw[i].Value-raw[i-1].Value;var b=y[i].Value-y[i-1].Value;
                Assert.True(a*b>=-1e-9);Assert.True(Math.Abs(b)<=Math.Abs(a)+1e-8);Assert.Equal(raw[i].Position,y[i].Position);
            }
        }
    }
    [Fact] public void RealLoggedScriptProducesReport() {
        var path=Environment.GetEnvironmentVariable("CTW_FILTER_SCRIPT");if(string.IsNullOrEmpty(path))return;
        var raw=JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("actions").EnumerateArray()
            .Select(p=>new Keyframe(p.GetProperty("at").GetDouble()/1000,p.GetProperty("pos").GetDouble()/100)).ToArray();
        var full=MotionScriptFilter.Apply(raw,Settings());
        Assert.Equal(raw.Select(p=>p.Position),full.Select(p=>p.Position));
        var reports=new List<object>();
        foreach(var (lo,hi) in new[]{(100d,110d),(167d,181d),(240d,246d)}) {
            var original=raw.Where(p=>p.Position>=lo-2&&p.Position<=hi+2).ToArray();
            var filtered=MotionScriptFilter.Apply(original,Settings());
            List<BufferedNode> Build(IReadOnlyList<Keyframe> points,bool filt) {
                var turns=TCodeBufferedPlan.NormalizeSpacing(TCodeBufferedPlan.TurningPoints(points,filt?1e-10:.01),1);
                var nodes=turns.Select(p=>new BufferedNode(0,(ulong)Math.Round(p.Position*1e6),(ulong)Math.Round(p.Value*9999))).ToList();
                BufferedReversalCurve.AssignOptimized(nodes);return nodes;
            }
            var a=Build(original,false);var b=Build(filtered,true);
            double[] Peaks(List<BufferedNode> n) {
                var peaks=new double[3];
                for(int i=0;i+1<n.Count;i++) {
                    if(n[i].Microseconds/1e6>hi||n[i+1].Microseconds/1e6<lo)continue;
                    var h=(n[i+1].Microseconds-n[i].Microseconds)/1e6;
                    var d=((double)n[i+1].Position-n[i].Position)/10000;
                    var A=n[i].AccelerationMicros/1e6*h*h;var B=n[i+1].AccelerationMicros/1e6*h*h;
                    double[] c=[0,0,A/2,0,35*d-5*A+2.5*B,-84*d+10*A-7*B,70*d-7.5*A+6.5*B,-20*d+2*A-2*B];
                    for(int order=1;order<=3;order++) {
                        c=c.Skip(1).Select((v,k)=>v*(k+1)).ToArray();
                        for(int j=0;j<=256;j++){var u=j/256d;double v=0;for(int k=c.Length-1;k>=0;k--)v=v*u+c[k];peaks[order-1]=Math.Max(peaks[order-1],Math.Abs(v)/Math.Pow(h,order));}
                    }
                }return peaks;
            }
            var before=Peaks(a);var after=Peaks(b);
            if(lo==100)Assert.Equal(original,filtered);
            else {Assert.True(after[1]<before[1]*.2);Assert.True(after[2]<before[2]*.2);}
            reports.Add(new {lo,hi,before,after,rawNodes=a.Count,filteredNodes=b.Count,
                points=original.Zip(filtered).Select(p=>new[]{p.First.Position,p.First.Value,p.Second.Value})});
        }
        var output=Environment.GetEnvironmentVariable("CTW_FILTER_REPORT");
        if(!string.IsNullOrEmpty(output))File.WriteAllText(output,JsonSerializer.Serialize(reports,new JsonSerializerOptions{WriteIndented=true}));
    }
}
