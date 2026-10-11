using MultiFunPlayer.Common;
using MultiFunPlayer.OutputTarget.TCodeBuffered;
using System.Globalization;

namespace MultiFunPlayer.TCode.Tests;

public class ProtocolAndPlanTests
{
    [Fact]
    public void CrcMatchesStandardVectorAndWireIsCultureIndependent()
    {
        Assert.Equal(0xcbf43926u, TCodeBufferedProtocol.Crc32("123456789"));
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var frame = TCodeBufferedProtocol.Encode([0, 4294967297, 0, 0]);
            Assert.StartsWith("!4,0,4294967297,0,0*", frame);
            Assert.True(TCodeBufferedProtocol.TryDecode(frame, out var values));
            Assert.Equal(new ulong[] { 0, 4294967297, 0, 0 }, values);
            Assert.False(TCodeBufferedProtocol.TryDecode(frame.Replace("4294967297", "4294967298"), out _));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("!3,0,1,0,0*0")]
    [InlineData("!4,0,-1,0,0*0")]
    [InlineData("!4,0,1,0,0*0")]
    [InlineData("!4,0,9007199254740992,0,0*0")]
    public void MalformedOrForeignFramesAreRejected(string wire)
        => Assert.False(TCodeBufferedProtocol.TryDecode(wire, out _));

    [Fact]
    public void InitialHoldAndTurningPointsMatchCtwPlanner()
    {
        Keyframe[] points = [new(0, 0), new(20.588, 0), new(21, .5), new(22, 1), new(23, .5), new(24, 0)];
        var turns = TCodeBufferedPlan.TurningPoints(points);
        Assert.Equal(new[] { points[0], points[1], points[3], points[5] }, turns);
        var plan = TCodeBufferedPlan.Create(Snapshot(points), 4000000)[0];
        Assert.Equal(new ulong[] { 4000000, 24588000, 26000000, 28000000 }, plan.Select(p => p.Microseconds));
        Assert.Equal(new ulong[] { 0, 0, 9999, 0 }, plan.Select(p => p.Position));
    }

    [Fact]
    public void SeekUsesDeviceSepticCurveAndAppliesOffsetsRateAndRange()
    {
        Keyframe[] points = [new(0, 0), new(2, 1), new(4, 0)];
        var source = Snapshot(points) with { Position = 1.5, Speed = 2 };
        source = source with { Axes = [source.Axes[0] with { Offset = 1, Invert = true, Minimum = .2, Maximum = .8 }] };
        var plan = TCodeBufferedPlan.Create(source, 10000000)[0];
        Assert.Equal((ulong)Math.Round((.2 + .6 * (1 - TCodeBufferedPlan.Smooth(.25))) * 9999), plan[0].Position);
        Assert.Equal(10750000UL, plan[1].Microseconds);
        Assert.Equal(11750000UL, plan[2].Microseconds);
        Assert.Equal(2000UL, plan[1].Position);
    }

    [Fact]
    public void SeekInterpolatesClippedOutputEndpoints()
    {
        var source = Snapshot([new(0, 0), new(2, 1)]);
        source = source with { Position = .5, Axes = [source.Axes[0] with { Scale = 4 }] };
        var plan = TCodeBufferedPlan.Create(source, 4000000)[0];
        Assert.Equal((ulong)Math.Round(9999 * TCodeBufferedPlan.Smooth(.25)), plan[0].Position);
    }

    [Fact]
    public void LongGapsAtSlowSpeedFitFirmwareIntervals()
    {
        var plan = TCodeBufferedPlan.Create(Snapshot([new(180, .5), new(400, 1)]) with { Speed = .5 }, 4000000)[0];
        Assert.All(plan.Zip(plan.Skip(1)), pair => Assert.InRange(pair.Second.Microseconds - pair.First.Microseconds, 1000UL, 60000000UL));
    }

    [Fact]
    public void UnsupportedLiveLimitIsNotSilentlyBypassed()
    {
        var source = Snapshot([new(0, 0), new(1, 1)]);
        source = source with { Axes = [source.Axes[0] with { UnsupportedReason = "script speed limit" }] };
        Assert.Throws<InvalidDataException>(() => TCodeBufferedPlan.Create(source, 4000000));
    }

    [Fact]
    public void SubMillisecondSegmentsAndNonFiniteValuesAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => TCodeBufferedPlan.Create(Snapshot([new(0, 0), new(.0005, 1)]), 4000000));
        Assert.Throws<InvalidDataException>(() => TCodeBufferedPlan.Create(Snapshot([new(0, double.NaN)]), 4000000));
    }

    [Fact]
    public void TimelineComparisonDetectsSeekSettingsAndScriptReplacement()
    {
        var source = Snapshot([new(0, 0), new(1, 1)]);
        Assert.True(source.HasSameTimeline(source with { Position = .5, IsPlaying = false }));
        Assert.False(source.HasSameTimeline(source with { Revision = 1 }));
        Assert.False(source.HasSameTimeline(source with { Speed = 2 }));
        Assert.False(source.HasSameTimeline(source with { Axes = [source.Axes[0] with { Minimum = .1 }] }));
        Assert.False(source.HasSameTimeline(source with { Axes = [source.Axes[0] with { Keyframes = source.Axes[0].Keyframes.ToArray() }] }));
    }

    internal static BufferedPlaybackSnapshot Snapshot(Keyframe[] points)
        => new(true, 0, 1, 0, [new("L0", points, 0, 1, false, .5)]);
}
