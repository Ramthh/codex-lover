using CodexAccountMonitor.Infrastructure;

namespace CodexAccountMonitor.Tests;

public sealed class SolMotionTimelineTests
{
    [Fact]
    public void AllFiveRoutinesHaveTheirOwnEqualLengthSection()
    {
        Assert.Equal(45, SolMotionTimeline.Duration);
        Assert.Equal(new[] { "Wave", "Build", "Heart", "Celebrate", "Peek" }, SolMotionTimeline.RoutineNames);
        for (var routine = 0; routine < 5; routine++)
            Assert.Equal((SolRoutine)routine, SolMotionTimeline.Sample(routine * 9 + 4).Routine);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidClockValuesAreRejected(double value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SolMotionTimeline.Sample(value));

    [Fact]
    public void SignedTimesWrapToTheSameDeterministicLoop()
    {
        foreach (var time in new[] { 0.0, .4, 2.25, 8.8, 16.75, 28.3, 43.0 })
        {
            AssertFramesClose(SolMotionTimeline.Sample(time), SolMotionTimeline.Sample(time + 45));
            AssertFramesClose(SolMotionTimeline.Sample(time), SolMotionTimeline.Sample(time - 45));
        }
        Assert.Equal(SolMotionTimeline.Sample(0), SolMotionTimeline.Sample(45));
        Assert.All(Values(SolMotionTimeline.Sample(double.MaxValue)), value => Assert.True(double.IsFinite(value)));
        Assert.All(Values(SolMotionTimeline.Sample(-double.MaxValue)), value => Assert.True(double.IsFinite(value)));
    }

    [Fact]
    public void EverySampleFitsSafeJointAndDisplayBounds()
    {
        // Sample the entire motion, including prop overshoots and narrow blink/hop peaks.
        for (var step = 0; step <= 45_000; step++)
        {
            var frame = SolMotionTimeline.Sample(step / 1000.0);
            Assert.All(Values(frame), value => Assert.True(double.IsFinite(value)));
            Assert.InRange(frame.HeadAngle, -30, 30);
            Assert.InRange(frame.BodyAngle, -15, 15);
            Assert.InRange(frame.LeftArmAngle, -70, 115);
            Assert.InRange(frame.RightArmAngle, -70, 100);
            Assert.InRange(frame.RightWristAngle, -18, 18);
            Assert.InRange(frame.LeftLegAngle, -30, 30);
            Assert.InRange(frame.RightLegAngle, -30, 30);
            Assert.InRange(frame.Crouch, 0, .08);
            Assert.InRange(frame.Jump, 0, .13);
            Assert.InRange(frame.Turn, -.3, .3);
            Assert.InRange(frame.EyeScale, .079, 1);
            Assert.InRange(frame.PropScale, -.000001, 1.2);
            Assert.InRange(frame.PropY, -.3, .2);
            Assert.InRange(frame.PropAngle, -30, 30);
            Assert.InRange(frame.ParticleProgress, 0, 1);
        }
    }

    [Fact]
    public void RoutineBoundariesReturnToNeutralWithoutJointOrPropJump()
    {
        for (var boundary = 0; boundary <= 45; boundary += 9)
        {
            var before = SolMotionTimeline.Sample(boundary - .0001);
            var after = SolMotionTimeline.Sample(boundary + .0001);
            Assert.Equal(0, before.PropScale, 6);
            Assert.Equal(0, after.PropScale, 6);
            foreach (var pair in Values(before).Zip(Values(after)))
                Assert.InRange(Math.Abs(pair.First - pair.Second), 0, .00001);
        }
    }

    [Theory]
    [InlineData(SolRoutine.Build)]
    [InlineData(SolRoutine.Heart)]
    [InlineData(SolRoutine.Celebrate)]
    [InlineData(SolRoutine.Peek)]
    public void PropsAnticipatePopOvershootSettleAndDisappear(SolRoutine routine)
    {
        var start = (int)routine * 9.0;
        Assert.True(SolMotionTimeline.Sample(start + .44).Crouch > .02);
        Assert.Equal(0, SolMotionTimeline.Sample(start + .44).PropScale, 6);
        Assert.True(SolMotionTimeline.Sample(start + 1.35).PropScale > 1);
        Assert.InRange(SolMotionTimeline.Sample(start + 2).PropScale, .95, 1.05);
        Assert.InRange(SolMotionTimeline.Sample(start + 8).PropScale, 0, .5);
        Assert.Equal(0, SolMotionTimeline.Sample(start + 8.8).PropScale, 6);
    }

    [Fact]
    public void WaveAndTypingMoveArmsIndependentlyOfTheBody()
    {
        var wave1 = SolMotionTimeline.Sample(2);
        var wave2 = SolMotionTimeline.Sample(2.15);
        Assert.True(Math.Abs(wave1.RightArmAngle - wave2.RightArmAngle) > 7);
        Assert.True(Math.Abs(wave1.RightWristAngle - wave2.RightWristAngle) > 12);
        Assert.Equal(wave1.BodyAngle, wave2.BodyAngle, 6);
        var typing1 = SolMotionTimeline.Sample(9 + 1.77);
        var typing2 = SolMotionTimeline.Sample(9 + 1.91);
        Assert.True(Math.Abs(typing1.LeftArmAngle - typing2.LeftArmAngle) > 10);
        Assert.Equal(typing1.LeftArmAngle + typing1.RightArmAngle,
            typing2.LeftArmAngle + typing2.RightArmAngle, 6);
        Assert.Equal(0, typing1.RightWristAngle);
        Assert.Equal(0, typing2.RightWristAngle);
    }

    [Fact]
    public void CelebrationPreparesThenHopsWithAlternatingLegsAndParticles()
    {
        var prepare = SolMotionTimeline.Sample(27 + 1.91);
        var first = SolMotionTimeline.Sample(27 + 2.42);
        var second = SolMotionTimeline.Sample(27 + 4.12);
        Assert.True(prepare.Crouch > .04);
        Assert.Equal(0, prepare.Jump);
        Assert.True(first.Jump > .12);
        Assert.True(second.Jump > .12);
        Assert.True(first.LeftLegAngle < first.RightLegAngle);
        Assert.True(second.LeftLegAngle > second.RightLegAngle);
        Assert.True(second.ParticleProgress > first.ParticleProgress);
    }

    private static void AssertFramesClose(SolMotionFrame first, SolMotionFrame second)
    {
        Assert.Equal(first.Routine, second.Routine);
        foreach (var pair in Values(first).Zip(Values(second)))
            Assert.InRange(Math.Abs(pair.First - pair.Second), 0, 1e-10);
    }

    private static double[] Values(SolMotionFrame frame) =>
        [frame.HeadAngle, frame.BodyAngle, frame.LeftArmAngle, frame.RightArmAngle,
            frame.RightWristAngle, frame.LeftLegAngle, frame.RightLegAngle, frame.Crouch, frame.Jump, frame.Turn,
            frame.EyeScale, frame.PropScale, frame.PropY, frame.PropAngle, frame.ParticleProgress];
}
