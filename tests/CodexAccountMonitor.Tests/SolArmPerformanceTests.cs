using CodexAccountMonitor.Infrastructure;

namespace CodexAccountMonitor.Tests;

public sealed class SolArmPerformanceTests
{
    [Theory]
    [InlineData(true, 110, 125)]
    [InlineData(false, 70, 55)]
    public void NeutralArmsHangDownWithStraightWrists(bool left, double upperAngle, double forearmAngle)
    {
        var pose = SolArmPerformance.Sample(SolPerformance.Sample(0), left);
        Assert.Equal(upperAngle, Angle(pose.Limb.Root, pose.Limb.Joint), 9);
        Assert.Equal(forearmAngle, Angle(pose.Limb.Joint, pose.Limb.End), 9);
        Assert.Equal(forearmAngle, pose.FingerAxisDegrees, 9);
        Assert.True(pose.Limb.Joint.Y > pose.Limb.Root.Y + 15);
        Assert.True(pose.Limb.End.Y > pose.Limb.Joint.Y + 17);
        Assert.Equal(0, pose.OpenAmount);
    }

    [Fact]
    public void WholePerformanceKeepsConnectedLengthsElbowsBelowShouldersAndWristsWithinThirtyDegrees()
    {
        for (var sample = 0; sample <= 60_000; sample++)
        {
            var frame = SolPerformance.Sample(sample / 1000.0);
            foreach (var left in new[] { true, false })
            {
                var pose = SolArmPerformance.Sample(frame, left);
                var limb = pose.Limb;
                Assert.Equal(17, Distance(limb.Root, limb.Joint), 9);
                Assert.Equal(22, Distance(limb.Joint, limb.End), 9);
                Assert.True(limb.Joint.Y > limb.Root.Y + 6, $"Raised elbow at {sample / 1000.0}s, left={left}");
                Assert.InRange(Wrap(pose.FingerAxisDegrees - Angle(limb.Joint, limb.End)), -30, 30);
                Assert.InRange(pose.OpenAmount, 0, 1);
                var anchor = SolKinematics.Rotate(new(left ? 100 : 140, left ? 169 : 165), new(120, 177), frame.BodyAngle);
                Assert.Equal(anchor.X + frame.BodyX, limb.Root.X, 9);
                Assert.Equal(anchor.Y + frame.BodyY, limb.Root.Y, 9);
                foreach (var point in new[] { limb.Root, limb.Joint, limb.End })
                {
                    Assert.True(double.IsFinite(point.X) && double.IsFinite(point.Y));
                    Assert.InRange(point.X, 60, 180);
                    Assert.InRange(point.Y, 120, 225);
                }
            }
        }
    }

    [Fact]
    public void EveryTransitionAvoidsElbowBranchFlipsAndFingerAngleJumps()
    {
        foreach (var left in new[] { true, false })
        {
            var previous = SolArmPerformance.Sample(SolPerformance.Sample(0), left);
            for (var sample = 1; sample <= 60_000; sample++)
            {
                var current = SolArmPerformance.Sample(SolPerformance.Sample(sample / 1000.0), left);
                Assert.InRange(Distance(previous.Limb.Joint, current.Limb.Joint), 0, .05);
                Assert.InRange(Distance(previous.Limb.End, current.Limb.End), 0, .15);
                Assert.InRange(Math.Abs(Wrap(Angle(current.Limb.Root, current.Limb.Joint) - Angle(previous.Limb.Root, previous.Limb.Joint))), 0, .1);
                Assert.InRange(Math.Abs(Wrap(Angle(current.Limb.Joint, current.Limb.End) - Angle(previous.Limb.Joint, previous.Limb.End))), 0, .35);
                // Compare the raw authored track, not its wrapped orientation:
                // even a full-turn numeric discontinuity would fail this check.
                Assert.InRange(Math.Abs(current.FingerAxisDegrees - previous.FingerAxisDegrees), 0, .4);
                previous = current;
            }
        }
    }

    [Theory]
    [InlineData(SolAct.Greet, false, 25)]
    [InlineData(SolAct.Build, true, 130)]
    [InlineData(SolAct.Build, false, 50)]
    [InlineData(SolAct.Heart, true, 145)]
    [InlineData(SolAct.Heart, false, 35)]
    [InlineData(SolAct.Celebrate, true, 155)]
    [InlineData(SolAct.Celebrate, false, 25)]
    [InlineData(SolAct.Inspect, false, 45)]
    public void IntentionalShoulderPosesArePreservedIndependentOfOldWristTargets(SolAct act, bool left, double expected)
    {
        var frame = SolPerformance.Sample((int)act * 12 + 5) with { BodyAngle = 0, BodyX = 0, BodyY = 0 };
        var pose = SolArmPerformance.Sample(frame, left);
        Assert.Equal(expected, Angle(pose.Limb.Root, pose.Limb.Joint), 9);
        Assert.Equal(pose, SolArmPerformance.Sample(frame with { LeftHand = new(-500, 500), RightHand = new(500, -500) }, left));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HeartPreservesForearmLengthAndUsesSmallFingerFlex(bool left)
    {
        var pose = SolArmPerformance.Sample(SolPerformance.Sample(29), left);
        Assert.Equal(22, Distance(pose.Limb.Joint, pose.Limb.End), 9);
        Assert.Equal(left ? -25 : 25, Wrap(pose.FingerAxisDegrees - Angle(pose.Limb.Joint, pose.Limb.End)), 9);
        Assert.Equal(0, pose.OpenAmount);
    }

    [Fact]
    public void TypingAlternatesVerticalPressesBetweenHands()
    {
        var forward = SolPerformance.Sample(12 + 2.1 + .62 * .25) with { BodyAngle = 0, BodyX = 0, BodyY = 0 };
        var backward = SolPerformance.Sample(12 + 2.1 + .62 * .75) with { BodyAngle = 0, BodyX = 0, BodyY = 0 };
        var leftTravel = SolArmPerformance.Sample(forward, true).Limb.End.Y - SolArmPerformance.Sample(backward, true).Limb.End.Y;
        var rightTravel = SolArmPerformance.Sample(forward, false).Limb.End.Y - SolArmPerformance.Sample(backward, false).Limb.End.Y;
        Assert.True(leftTravel > .5);
        Assert.True(rightTravel < -.5);
        Assert.Equal(leftTravel, -rightTravel, 9);
    }

    [Fact]
    public void HandsOpenOnlyForWaveAndCelebrationAndRelaxAtEverySeam()
    {
        foreach (var act in Enum.GetValues<SolAct>())
        foreach (var left in new[] { true, false })
        {
            var active = SolArmPerformance.Sample(SolPerformance.Sample((int)act * 12 + 5), left);
            Assert.Equal(act == SolAct.Celebrate || (act == SolAct.Greet && !left) ? 1 : 0, active.OpenAmount);
            var seam = SolArmPerformance.Sample(SolPerformance.Sample((int)act * 12), left);
            Assert.Equal(SolArmPerformance.Sample(SolPerformance.Sample(0), left), seam);
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteInputIsRejected(double invalid)
    {
        var frame = SolPerformance.Sample(0);
        foreach (var changed in new[] { frame with { Phase = invalid }, frame with { Energy = invalid },
            frame with { BodyX = invalid }, frame with { BodyY = invalid }, frame with { BodyAngle = invalid } })
            Assert.Throws<ArgumentOutOfRangeException>(() => SolArmPerformance.Sample(changed, false));
    }

    private static double Angle(SolPoint start, SolPoint end) => Math.Atan2(end.Y - start.Y, end.X - start.X) * 180 / Math.PI;
    private static double Distance(SolPoint a, SolPoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    private static double Wrap(double value)
    {
        value %= 360;
        if (value > 180) value -= 360;
        if (value < -180) value += 360;
        return value;
    }
}
