using MultiFunPlayer.Common;
using MultiFunPlayer.OutputTarget.TCodeBuffered;

namespace MultiFunPlayer.TCode.Tests;

public class SessionPlanTests
{
    [Fact]
    public void DoubleSpeedStrokeKeepsTimesAndAmplitudeAboveFormerNumericCap()
    {
        var plan = TCodeBufferedPlan.CreateSession(
            ProtocolAndPlanTests.Snapshot([new(0, .82), new(.058, .15), new(.116, .82)]), Pose());
        Assert.InRange(plan.Limits.Translation.Jerk, 183932, 183934);
        Assert.True(plan.Limits.Encode()[2] > 100000000000UL);
        Assert.Equal(new ulong[] { 8199, 1500, 8199 }, plan.Nodes[0].Select(n => n.Position));
        Assert.Equal(new ulong[] { 0, 58000, 116000 }, plan.Nodes[0].Select(n => n.Microseconds - plan.PositioningMicroseconds));
        Verify(plan, Pose());
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1e10)]
    public void NonRepresentableEnvelopeStillFails(double value)
        => Assert.Throws<InvalidDataException>(() => new BufferedSessionLimits(new(1, 1, value), new(1, 1, 1)).Encode());
    [Fact]
    public void MissingTwistMotorIgnoresScriptBeforeValidationAndLimitCalculation()
    {
        Keyframe[] frames = [new(0, .4), new(2, .6)];
        var source = ProtocolAndPlanTests.Snapshot(frames) with
        {
            Axes = TCodeBufferedPlan.AxisNames.Select(name => new BufferedScriptAxis(name, frames, 0, 1, false, .5)).ToArray()
        };
        var all = TCodeBufferedPlan.CreateSession(source, Pose(), 63);
        Assert.All(all.Nodes, nodes => Assert.NotEmpty(nodes));
        var badTwist = source with { Axes = source.Axes.Select(a => a.Name == "R0" ? a with { UnsupportedReason = "invalid unused script" } : a).ToArray() };
        var filtered = TCodeBufferedPlan.CreateSession(badTwist, Pose(), 55);
        Assert.Empty(filtered.Nodes[3]);
        foreach (var axis in new[] { 0, 1, 2, 4, 5 }) Assert.NotEmpty(filtered.Nodes[axis]);
        Assert.Equal(55, filtered.Nodes.Select((nodes, i) => nodes.Count > 0 ? 1 << i : 0).Aggregate((a, b) => a | b));
        var removed = source with { Axes = source.Axes.Where(a => a.Name != "R0").ToArray() };
        var expected = TCodeBufferedPlan.CreateSession(removed, Pose());
        Assert.Equal(expected.Limits, filtered.Limits);
        Assert.Equal(expected.PositioningMicroseconds, filtered.PositioningMicroseconds);
        Assert.Throws<InvalidDataException>(() => TCodeBufferedPlan.CreateSession(source, Pose(), 0));
    }
    private static BufferedDevicePose Pose(double value = .5) => new(Enumerable.Repeat(value, 6).ToArray());

    [Fact]
    public void FastScriptRetainsFullAmplitudeAndOriginalTimes()
    {
        var plan = TCodeBufferedPlan.CreateSession(ProtocolAndPlanTests.Snapshot([new(0, 0), new(.2, 1), new(.4, 0)]), Pose());
        Assert.Equal(new ulong[] { 0, 9999, 0 }, plan.Nodes[0].Select(n => n.Position));
        Assert.Equal(new ulong[] { 0, 200000, 400000 }, plan.Nodes[0].Select(n => n.Microseconds - plan.PositioningMicroseconds));
        Assert.InRange(plan.Limits.Translation.Velocity, 11, 12);
        Assert.InRange(plan.Limits.Translation.Jerk, 6695, 6696);
        Verify(plan, Pose());
    }

    [Fact]
    public void SpeedScalesVelocityAccelerationAndJerkWithoutShrinking()
    {
        var source = ProtocolAndPlanTests.Snapshot([new(0, 0), new(2, 1), new(4, 0)]);
        var slow = TCodeBufferedPlan.CreateSession(source, Pose());
        var fast = TCodeBufferedPlan.CreateSession(source with { Speed = 2 }, Pose());
        var a = slow.Limits.Translation; var b = fast.Limits.Translation;
        Assert.InRange(Math.Abs(b.Velocity - 2 * a.Velocity), 0, .000002);
        Assert.InRange(Math.Abs(b.Acceleration - 4 * a.Acceleration), 0, .000004);
        Assert.InRange(Math.Abs(b.Jerk - 8 * a.Jerk), 0, .000008);
        Assert.Equal(slow.Nodes[0].Select(n => n.Position), fast.Nodes[0].Select(n => n.Position));
        Assert.Equal(2000000UL, fast.Nodes[0][^1].Microseconds - fast.PositioningMicroseconds);
        Verify(fast, Pose());
    }

    [Fact]
    public void NewScriptDoesNotInheritPreviousEnvelope()
    {
        var fast = TCodeBufferedPlan.CreateSession(ProtocolAndPlanTests.Snapshot([new(0, 0), new(.2, 1)]), Pose());
        var slow = TCodeBufferedPlan.CreateSession(ProtocolAndPlanTests.Snapshot([new(0, 0), new(10, 1)]), Pose());
        Assert.True(fast.Limits.Translation.Jerk > slow.Limits.Translation.Jerk * 100000);
        Verify(slow, Pose());
    }

    [Fact]
    public void TranslationAndRotationAreDerivedIndependently()
    {
        Keyframe[] linear = [new(0, .1), new(1, .9), new(2, .1)];
        Keyframe[] angular = [new(0, .4), new(2, .6), new(4, .4)];
        var source = ProtocolAndPlanTests.Snapshot(linear) with
        {
            Axes = [new("L0", linear, 0, 1, false, .5), new("R0", angular, 0, 1, false, .5), new("R2", angular, 0, 1, false, .5)]
        };
        var plan = TCodeBufferedPlan.CreateSession(source, Pose());
        Assert.True(plan.Limits.Translation.Velocity > plan.Limits.Rotation.Velocity * 7);
        Assert.Equal(plan.Nodes[3].Select(n => n.Position), plan.Nodes[5].Select(n => n.Position));
        Verify(plan, Pose());
    }

    [Fact]
    public void LongPositioningIsSplitUnderTheSlowScriptsOwnLimits()
    {
        var source = ProtocolAndPlanTests.Snapshot([new(0, 1), new(60, .99)]);
        var plan = TCodeBufferedPlan.CreateSession(source, Pose(0));
        Assert.True(plan.PositioningMicroseconds > 60000000);
        Assert.True(plan.Nodes[0].Count > 2);
        Verify(plan, Pose(0));
    }

    [Theory]
    [InlineData(.5)]
    [InlineData(0)]
    public void StaticScriptHasFiniteLimitsAndCanPosition(double initial)
    {
        var plan = TCodeBufferedPlan.CreateSession(ProtocolAndPlanTests.Snapshot([new(0, .5), new(10, .5)]), Pose(initial));
        if (initial == .5) Assert.Equal(1000UL, plan.PositioningMicroseconds);
        else Assert.InRange(plan.PositioningMicroseconds, 1000000UL, 2000000UL);
        Assert.All(plan.Limits.Encode(), v => Assert.InRange(v, 1UL, 100000000000UL));
        Verify(plan, Pose(initial));
    }

    [Fact]
    public void ResumeNearTargetUsesRequiredMotionTimeInsteadOfOneSecondFloor()
    {
        var pose = Pose(.49);
        var plan = TCodeBufferedPlan.CreateSession(
            ProtocolAndPlanTests.Snapshot([new(0, .5), new(.2, .6), new(.4, .5)]), pose);
        Assert.InRange(plan.PositioningMicroseconds, 50000UL, 200000UL);
        Assert.Equal(750000UL, TCodeBufferedPlayback.PreloadLeadMicroseconds(plan, 8));
        Verify(plan, pose);
    }

    [Fact]
    public void ExtremeDemandFailsBeforeBeginWithoutChangingAmplitude()
    {
        Assert.Throws<InvalidDataException>(() => TCodeBufferedPlan.CreateSession(
            ProtocolAndPlanTests.Snapshot([new(0, 0), new(.001, 1)]), Pose()));
    }

    [Fact]
    public void PoseQueryContainsNoDeviceLimits()
    {
        ulong[] f = [101, 1, 99, 0, 0, 0, 1, 500000, 500000, 500000, 500000, 500000, 500000];
        Assert.Equal(Enumerable.Repeat(.5, 6), BufferedDevicePose.Decode(f).Positions);
        f[6] = 2;
        Assert.Throws<InvalidDataException>(() => BufferedDevicePose.Decode(f));
        f[6] = 1; f[7] = 1000001;
        Assert.Throws<InvalidDataException>(() => BufferedDevicePose.Decode(f));
    }

    private static void Verify(BufferedSessionPlan plan, BufferedDevicePose device)
    {
        for (var axis = 0; axis < 6; axis++)
        {
            var previous = device.Positions[axis]; var time = 0UL;
            foreach (var node in plan.Nodes[axis])
            {
                Assert.InRange(node.Microseconds - time, 1000UL, 60000000UL);
                var distance = Math.Abs(node.Position / 10000d - previous);
                Assert.True(distance <= plan.Limits.ForAxis(axis).AllowedDistance((node.Microseconds - time) / 1e6));
                previous = node.Position / 10000d; time = node.Microseconds;
            }
        }
    }
}
