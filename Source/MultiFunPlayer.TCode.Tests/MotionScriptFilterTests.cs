using MultiFunPlayer.Common;
using MultiFunPlayer.OutputTarget.TCodeBuffered;

namespace MultiFunPlayer.TCode.Tests;

public class MotionScriptFilterTests
{
    private static readonly MotionFilterSettings Enabled = new(true, 1, 100);

    [Fact]
    public void DisabledAndBelowThresholdKeepOriginalScriptExactly()
    {
        Keyframe[] slow = [new(0, .2), new(2, .8), new(4, .2)];
        Assert.Same(slow, MotionScriptFilter.Apply(slow, Enabled));
        var vibration = Vibration;
        Assert.Same(vibration, MotionScriptFilter.Apply(vibration, Enabled with { Enabled = false }));
    }

    private static Keyframe[] Vibration => Enumerable.Range(0, 401)
        .Select(i => new Keyframe(i * .01, .5 + (i % 2 == 0 ? -.1 : .1))).ToArray();

    [Fact]
    public void RapidReversalsAreAttenuatedWithoutMovingTimesOrMutatingInput()
    {
        var source = Vibration;
        var original = source.ToArray();
        var result = MotionScriptFilter.Apply(source, Enabled);
        var center = result.Where(p => p.Position is > 1 and < 3).ToArray();
        Assert.True(center.Max(p => p.Value) - center.Min(p => p.Value) < .02);
        Assert.Equal(source.Select(p => p.Position), result.Select(p => p.Position));
        Assert.Equal(source[0], result[0]);
        Assert.Equal(source[^1], result[^1]);
        Assert.Equal(original, source);
        Assert.All(result, p => Assert.InRange(p.Value, 0, 1));
    }

    [Fact]
    public void SlowLargeStrokeSurvivesWhileItsSmallFastVibrationIsRemoved()
    {
        var source = Enumerable.Range(0, 1001).Select(i =>
        {
            var t = i * .01;
            return new Keyframe(t, .5 + .3 * Math.Sin(t * Math.PI / 5) + .03 * Math.Sin(t * 40 * Math.PI));
        }).ToArray();
        var result = MotionScriptFilter.Apply(source, Enabled);
        var middle = result.Where(p => p.Position is > 1 and < 9).ToArray();
        var error = middle.Average(p => Math.Abs(p.Value - (.5 + .3 * Math.Sin(p.Position * Math.PI / 5))));
        Assert.InRange(error, 0, .005);
        Assert.True(middle.Max(p => p.Value) - middle.Min(p => p.Value) > .55);
    }

    [Fact]
    public void ThresholdAccountsForPlaybackSpeedAndScriptScale()
    {
        Keyframe[] source = Enumerable.Range(0, 41).Select(i => new Keyframe(i * .1, i % 2 == 0 ? .4 : .5)).ToArray();
        Assert.Same(source, MotionScriptFilter.Apply(source, Enabled));
        var fast = MotionScriptFilter.Apply(source, Enabled, 2);
        Assert.NotSame(source, fast);
        Assert.Same(source, MotionScriptFilter.Apply(source, Enabled, 2, .25));
    }

    [Fact]
    public void IrregularTimesAndLongHoldsStayFiniteAndBounded()
    {
        Keyframe[] source = [new(0, .5), new(.001, .9), new(.00100001, .1), new(.005, .8), new(1, .5), new(100, .5)];
        var result = MotionScriptFilter.Apply(source, Enabled);
        Assert.All(result, p => Assert.True(double.IsFinite(p.Value) && p.Value is >= 0 and <= 1));
        Assert.Equal(.5, result[^1].Value);
    }

    [Fact]
    public void AllSixAxesProduceUsableBufferedPlans()
    {
        var axes = MotionScriptFilter.Axes.Select(name => new BufferedScriptAxis(name,
            MotionScriptFilter.Apply(Vibration, Enabled), 0, 1, false, .5)).ToArray();
        var snapshot = new BufferedPlaybackSnapshot(false, 0, 1, 1, axes);
        var plan = TCodeBufferedPlan.Create(snapshot, 1000000);
        Assert.Equal(6, plan.Count(a => a.Count > 0));
        Assert.All(plan, axis => Assert.True(axis.Zip(axis.Skip(1)).All(p => p.Second.Microseconds > p.First.Microseconds)));
    }

    [Theory]
    [InlineData(double.NaN, 100)]
    [InlineData(0, 100)]
    [InlineData(1, 0)]
    [InlineData(1, double.PositiveInfinity)]
    public void InvalidSettingsCannotEnterOutput(double threshold, double time)
        => Assert.Throws<ArgumentOutOfRangeException>(() => MotionScriptFilter.Apply(Vibration, new(true, threshold, time)));
}
