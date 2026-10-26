using CodexAccountMonitor.Infrastructure;

namespace CodexAccountMonitor.Tests;

public sealed class SolPerformanceTests
{
    [Fact]
    public void AllActsAreNamedAndHaveTwelveSecondSections()
    {
        Assert.Equal(60, SolPerformance.Duration);
        Assert.Equal(new[] { "Greet", "Build", "Heart", "Celebrate", "Inspect" }, SolPerformance.ActNames);
        for (var act = 0; act < 5; act++)
        {
            var frame = SolPerformance.Sample(act * 12 + 5);
            Assert.Equal((SolAct)act, frame.Act);
            Assert.Equal(5, frame.Phase);
            Assert.Equal(1, frame.Energy);
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteClockValuesAreRejected(double time) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SolPerformance.Sample(time));

    [Fact]
    public void NegativeAndRepeatedTimesMatchAndExtremeFiniteTimeIsSafe()
    {
        foreach (var time in new[] { 0.0, .55, 3.23, 14.62, 28.5, 39.575, 57.75 })
        {
            SamePose(SolPerformance.Sample(time), SolPerformance.Sample(time + 60), 1e-10);
            SamePose(SolPerformance.Sample(time), SolPerformance.Sample(time - 60), 1e-10);
        }
        Assert.All(PoseValues(SolPerformance.Sample(double.MaxValue)), value => Assert.True(double.IsFinite(value)));
        Assert.All(PoseValues(SolPerformance.Sample(-double.MaxValue)), value => Assert.True(double.IsFinite(value)));
    }

    [Fact]
    public void CompletePerformanceIsFiniteBoundedAndReachable()
    {
        for (var step = 0; step <= 60_000; step++)
        {
            var frame = SolPerformance.Sample(step / 1000.0);
            Assert.All(PoseValues(frame), value => Assert.True(double.IsFinite(value)));
            Assert.InRange(frame.Phase, 0, 12);
            Assert.InRange(frame.Energy, 0, 1);
            Assert.InRange(frame.BodyX, -4, 4);
            Assert.InRange(frame.BodyY, -15.01, 6);
            Assert.InRange(frame.BodyAngle, -3, 3);
            Assert.InRange(frame.HeadAngle, -6, 9);
            Assert.InRange(frame.HeadX, -3, 3);
            Assert.InRange(frame.HeadY, -2, 3);
            Assert.InRange(frame.GazeX, -1, 1);
            Assert.InRange(frame.GazeY, -1, 1);
            Assert.InRange(frame.EyeOpen, 0, 1);
            Assert.InRange(frame.PropScale, 0, 1);
            Assert.InRange(frame.FingerCurl, 0, 1);
            Assert.InRange(frame.Landing, 0, 1);
            foreach (var point in new[] { frame.LeftHand, frame.RightHand, frame.LeftFoot, frame.RightFoot })
            {
                Assert.InRange(point.X, 65, 180);
                Assert.InRange(point.Y, 95, 233);
            }
            Assert.InRange(frame.Prop.X, 65, 210);
            Assert.InRange(frame.Prop.Y, 95, 233);
            Assert.InRange(Distance(frame.LeftHand, Joint(100, 169, frame)), 8.249, 40.751);
            Assert.InRange(Distance(frame.RightHand, Joint(140, 165, frame)), 8.249, 40.751);
            // The compact shin must reach the requested planted ankle within 24px;
            // silently clamping here would lift or slide an apparently grounded foot.
            Assert.InRange(Distance(frame.LeftFoot, Joint(110, 199, frame)), 1.01, 24);
            Assert.InRange(Distance(frame.RightFoot, Joint(133, 199, frame)), 1.01, 24);
        }
    }

    [Fact]
    public void NeutralLegsAreNearlyExtendedRatherThanPermanentlySquatting()
    {
        var neutral = SolPerformance.Sample(0);
        const double maximumShinLength = 24;
        Assert.InRange(Distance(neutral.LeftFoot, Joint(110, 199, neutral)) / maximumShinLength, .900001, 1);
        Assert.InRange(Distance(neutral.RightFoot, Joint(133, 199, neutral)) / maximumShinLength, .900001, 1);
    }

    [Fact]
    public void FeetStayPlantedExceptDuringCelebrationFlight()
    {
        for (var step = 0; step <= 6_000; step++)
        {
            var frame = SolPerformance.Sample(step / 100.0);
            var flying = frame.Act == SolAct.Celebrate &&
                (frame.Phase > 3 && frame.Phase < 4.15 || frame.Phase > 6.2 && frame.Phase < 7.45);
            if (flying) continue;
            Assert.Equal(new SolPoint(104, 220), frame.LeftFoot);
            Assert.Equal(new SolPoint(141, 220), frame.RightFoot);
        }
    }

    [Fact]
    public void EveryActAndLoopBoundaryReturnsToCommonNeutralPose()
    {
        var neutral = SolPerformance.Sample(0);
        for (var boundary = 0; boundary <= 60; boundary += 12)
        {
            SamePose(neutral, SolPerformance.Sample(boundary), 1e-10, compareAct: false);
            SamePose(SolPerformance.Sample(boundary - .0001), SolPerformance.Sample(boundary + .0001), .000001, compareAct: false);
        }
    }

    [Fact]
    public void TypingPressesAlternateAndLeaveRoomForFingersAboveKeyboard()
    {
        for (var step = 0; step < 100; step++)
        {
            var frame = SolPerformance.Sample(15 + step / 100.0);
            foreach (var left in new[] { true, false })
            {
                var arm = SolArmPerformance.Sample(frame, left);
                // Both original closed hands extend about 15px beyond the wrist.
                var radians = arm.FingerAxisDegrees * Math.PI / 180;
                var tip = new SolPoint(arm.Limb.End.X + 15 * Math.Cos(radians),
                    arm.Limb.End.Y + 15 * Math.Sin(radians));
                Assert.InRange(tip.X, frame.Prop.X - 32, frame.Prop.X + 32);
                Assert.InRange(tip.Y, frame.Prop.Y + 4, frame.Prop.Y + 16);
                Assert.True(arm.Limb.End.Y < frame.Prop.Y);
            }
            Assert.True(frame.GazeY > .5);
        }
    }

    [Fact]
    public void HeartStaysBetweenTheConnectedHandsDuringPresentation()
    {
        for (var step = 0; step <= 700; step++)
        {
            var frame = SolPerformance.Sample(27 + step / 100.0);
            Assert.True(frame.LeftHand.X < frame.Prop.X);
            Assert.True(frame.RightHand.X > frame.Prop.X);
            Assert.InRange(Distance(frame.LeftHand, frame.Prop), 8, 25);
            Assert.InRange(Distance(frame.RightHand, frame.Prop), 8, 25);
            Assert.InRange(frame.LeftHand.Y - frame.Prop.Y, 0, 10);
            Assert.InRange(frame.RightHand.Y - frame.Prop.Y, 0, 10);
        }
    }

    [Fact]
    public void CelebrationCoilsJumpsAndLandsWithoutLeavingFeetBehind()
    {
        var coil = SolPerformance.Sample(36 + 2.8);
        var airborne = SolPerformance.Sample(36 + 3.575);
        var landing = SolPerformance.Sample(36 + 4.3);
        Assert.InRange(coil.BodyY, 1.79, 1.81);
        Assert.Equal(220, coil.LeftFoot.Y);
        Assert.InRange(airborne.BodyY, -15.01, -14.99);
        Assert.Equal(205, airborne.LeftFoot.Y, 6);
        Assert.Equal(205, airborne.RightFoot.Y, 6);
        Assert.True(landing.Landing > .99);
        Assert.InRange(landing.BodyY, 2.19, 2.21);
        Assert.Equal(220, landing.LeftFoot.Y);
    }

    [Fact]
    public void CelebrationGiftStaysBesideRobotWhileItJumps()
    {
        // The gift belongs on the stage, not attached to Sol's head or pelvis.
        foreach (var phase in new[] { 2.1, 2.8, 3.575, 4.3, 6.825, 7.6, 9.9 })
        {
            var frame = SolPerformance.Sample(36 + phase);
            Assert.Equal(1, frame.Energy);
            Assert.Equal(new SolPoint(193, 221), frame.Prop);
            Assert.True(frame.Prop.X > frame.RightFoot.X + 45);
        }
        Assert.True(SolPerformance.Sample(36 + 3.575).BodyY < -14);
        Assert.True(SolPerformance.Sample(36 + 4.3).BodyY > 2);
    }

    [Fact]
    public void PropsAppearAfterAnticipationAndEaseAwayInLastTwoSeconds()
    {
        foreach (var act in new[] { SolAct.Build, SolAct.Heart, SolAct.Celebrate, SolAct.Inspect })
        {
            var start = (int)act * 12;
            Assert.True(SolPerformance.Sample(start + .55).BodyY > 1);
            Assert.Equal(0, SolPerformance.Sample(start + .55).PropScale);
            Assert.Equal(1, SolPerformance.Sample(start + 3).PropScale);
            Assert.InRange(SolPerformance.Sample(start + 11).PropScale, .49, .51);
            Assert.InRange(SolPerformance.Sample(start + 11.99).PropScale, 0, .00001);
        }
    }

    [Fact]
    public void BlinkDoesNotMoveBodyOrInterruptTheHeldProp()
    {
        var open = SolPerformance.Sample(12 + 3.0);
        var blink = SolPerformance.Sample(12 + 3.23);
        Assert.True(open.EyeOpen > .99);
        Assert.True(blink.EyeOpen < .04);
        Assert.Equal(open.PropScale, blink.PropScale);
        Assert.InRange(Math.Abs(open.BodyY - blink.BodyY), 0, .1);
    }

    private static SolPoint Joint(double x, double y, SolPerformanceFrame frame)
    {
        var radians = frame.BodyAngle * Math.PI / 180;
        return new(120 + frame.BodyX + (x - 120) * Math.Cos(radians) - (y - 177) * Math.Sin(radians),
            177 + frame.BodyY + (x - 120) * Math.Sin(radians) + (y - 177) * Math.Cos(radians));
    }

    private static double Distance(SolPoint first, SolPoint second) =>
        Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));

    private static void SamePose(SolPerformanceFrame first, SolPerformanceFrame second, double tolerance, bool compareAct = true)
    {
        if (compareAct) Assert.Equal(first.Act, second.Act);
        foreach (var pair in PoseValues(first).Zip(PoseValues(second)))
            Assert.InRange(Math.Abs(pair.First - pair.Second), 0, tolerance);
    }

    private static double[] PoseValues(SolPerformanceFrame frame) =>
        [frame.Energy, frame.BodyX, frame.BodyY, frame.BodyAngle, frame.HeadAngle, frame.HeadX, frame.HeadY,
            frame.GazeX, frame.GazeY, frame.EyeOpen, frame.LeftHand.X, frame.LeftHand.Y,
            frame.RightHand.X, frame.RightHand.Y, frame.LeftFoot.X, frame.LeftFoot.Y,
            frame.RightFoot.X, frame.RightFoot.Y, frame.Prop.X, frame.Prop.Y, frame.PropScale,
            frame.PropAngle, frame.FingerCurl, frame.Landing];
}
