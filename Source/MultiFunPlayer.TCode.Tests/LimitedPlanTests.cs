using MultiFunPlayer.Common;
using MultiFunPlayer.OutputTarget.TCodeBuffered;

namespace MultiFunPlayer.TCode.Tests;

public class LimitedPlanTests
{
    private static TCodeBufferedLimits Limits(double v = 1, double a = 4, double j = 20)
        => new(1, new(v, a, j), new(v, a, j), Enumerable.Repeat(.5, 6).ToArray());

    [Fact]
    public void FeasibleScriptRetainsFullAmplitudeAndRelativeTimes()
    {
        var plan = TCodeBufferedPlan.CreateLimited(ProtocolAndPlanTests.Snapshot([new(0, 0), new(4, 1), new(8, 0)]), Limits());
        Assert.Equal(1, plan.AmplitudeScale);
        Assert.Equal(new ulong[] { 0, 9999, 0 }, plan.Nodes[0].Select(n => n.Position));
        Assert.Equal(new ulong[] { 0, 4000000, 8000000 }, plan.Nodes[0].Select(n => n.Microseconds - plan.PositioningMicroseconds));
        Verify(plan, Limits());
    }

    [Theory]
    [InlineData(.05, 100, 1000)]
    [InlineData(100, .05, 1000)]
    [InlineData(100, 100, .05)]
    [InlineData(.001, .001, .001)]
    public void EachDeviceLimitConstrainsQuantizedMotion(double v, double a, double j)
    {
        var limits = Limits(v, a, j);
        var source = ProtocolAndPlanTests.Snapshot([new(0, 0), new(.2, 1), new(.4, 0)]);
        var plan = TCodeBufferedPlan.CreateLimited(source, limits);
        Assert.True(plan.AmplitudeScale < 1);
        Verify(plan, limits);
    }

    [Fact]
    public void FasterPlaybackShrinksInsteadOfStretchingTimeline()
    {
        var source = ProtocolAndPlanTests.Snapshot([new(0, 0), new(2, 1), new(4, 0)]);
        var slow = TCodeBufferedPlan.CreateLimited(source, Limits());
        var fast = TCodeBufferedPlan.CreateLimited(source with { Speed = 2 }, Limits());
        Assert.True(fast.AmplitudeScale < slow.AmplitudeScale);
        Assert.Equal(2000000UL, fast.Nodes[0][^1].Microseconds - fast.PositioningMicroseconds);
        Verify(fast, Limits());
    }

    [Fact]
    public void LongPositioningIsSplitAndVerifiedUnderLowAcceleration()
    {
        var limits = Limits(.001, .001, .001) with { Positions = new double[6] };
        var source = ProtocolAndPlanTests.Snapshot([new(0, 1), new(2, 1)]);
        var plan = TCodeBufferedPlan.CreateLimited(source, limits);
        Assert.True(plan.PositioningMicroseconds > 60000000);
        Assert.True(plan.Nodes[0].Count > 2);
        Verify(plan, limits);
    }

    [Fact]
    public void MixedAxesUseTheirCorrectLimitsAndOneAmplitudeScale()
    {
        Keyframe[] points = [new(0, .1), new(1, .9), new(2, .1)];
        var source = ProtocolAndPlanTests.Snapshot(points) with
        {
            Axes = [new("L0", points, 0, 1, false, .5), new("R0", points, 0, 1, false, .5), new("R2", points, 0, 1, false, .5)]
        };
        var limits = Limits() with { Rotation = new(.01, .1, 1) };
        var plan = TCodeBufferedPlan.CreateLimited(source, limits);
        Assert.Equal(plan.Nodes[0].Select(x => x.Position), plan.Nodes[3].Select(x => x.Position));
        Verify(plan, limits);
    }

    [Fact]
    public void CapabilitiesRejectUnknownModelAndZeroLimits()
    {
        ulong[] f = [101, 1, 99, 0, 0, 0, 1, 1, 1000000, 4000000, 20000000,
            1000000, 4000000, 20000000, 500000, 500000, 500000, 500000, 500000, 500000];
        Assert.Equal(1d, TCodeBufferedLimits.Decode(f).Translation.Velocity);
        f[7] = 2;
        Assert.Throws<InvalidDataException>(() => TCodeBufferedLimits.Decode(f));
        f[7] = 1; f[8] = 0;
        Assert.Throws<InvalidDataException>(() => TCodeBufferedLimits.Decode(f));
    }

    private static void Verify(LimitedBufferedPlan plan, TCodeBufferedLimits limits)
    {
        for (var axis = 0; axis < 6; axis++)
        {
            var previous = limits.Positions[axis];
            var time = 0UL;
            foreach (var node in plan.Nodes[axis])
            {
                Assert.InRange(node.Microseconds - time, 1000UL, 60000000UL);
                var seconds = (node.Microseconds - time) / 1e6;
                var distance = Math.Abs(node.Position / 10000d - previous);
                Assert.True(distance <= limits.ForAxis(axis).AllowedDistance(seconds));
                previous = node.Position / 10000d;
                time = node.Microseconds;
            }
        }
    }
}
