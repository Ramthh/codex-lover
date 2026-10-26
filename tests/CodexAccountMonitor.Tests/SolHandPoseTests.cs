using CodexAccountMonitor.Infrastructure;

namespace CodexAccountMonitor.Tests;

public sealed class SolHandPoseTests
{
    [Theory]
    [InlineData(0, 103, false)]
    [InlineData(90, 103, false)]
    [InlineData(155, 103, false)]
    [InlineData(-90, -80, false)]
    [InlineData(-35, -80, false)]
    [InlineData(90, -80, true)]
    [InlineData(-135, -80, true)]
    public void RestingFingersContinueElbowToWrist(double forearm, double sourceAxis, bool mirror)
    {
        var elbow = new SolPoint(80, 120);
        var wrist = Wrist(elbow, forearm);
        var rotation = SolHandPose.RotationDegrees(elbow, wrist, sourceAxis, mirror, -20, 0);
        AssertDirection(forearm, TransformedFingerAxis(sourceAxis, mirror, rotation));
    }

    [Fact]
    public void HorizontalMirrorUsesOneHundredEightyMinusOriginalDirection()
    {
        const double sourceAxis = -80;
        const double forearm = 90;
        var rotation = SolHandPose.RotationDegrees(new SolPoint(0, 0), new SolPoint(0, 25), sourceAxis, true, forearm, 0);
        Assert.Equal(-170, rotation, 10);
        var sourceFinger = Wrist(new SolPoint(0, 0), sourceAxis);
        var mirroredFinger = new SolPoint(-sourceFinger.X, sourceFinger.Y);
        var rotatedFinger = SolKinematics.Rotate(mirroredFinger, new SolPoint(0, 0), rotation);
        Assert.Equal(0, rotatedFinger.X, 10);
        Assert.True(rotatedFinger.Y > 0);
        AssertDirection(forearm, Math.Atan2(rotatedFinger.Y, rotatedFinger.X) * 180 / Math.PI);
    }

    [Theory]
    [InlineData(-110, -90, -80, false)]
    [InlineData(115, 0, 103, false)]
    [InlineData(65, 180, -80, false)]
    [InlineData(100, 75, -80, true)]
    [InlineData(80, 105, -80, false)]
    [InlineData(-40, -70, -80, false)]
    public void FullBlendPointsAtIntentionalGesture(double forearm, double desired, double sourceAxis, bool mirror)
    {
        var elbow = new SolPoint(14, 19);
        var rotation = SolHandPose.RotationDegrees(elbow, Wrist(elbow, forearm), sourceAxis, mirror, desired, 1);
        AssertDirection(desired, TransformedFingerAxis(sourceAxis, mirror, rotation));
    }

    [Theory]
    [InlineData(179, -179, .5, 180)]
    [InlineData(-179, 179, .5, -180)]
    [InlineData(170, -170, .25, 175)]
    [InlineData(-170, 170, .25, -175)]
    [InlineData(30, 90, .5, 60)]
    public void PartialBlendTakesShortestArcAcrossAngleWrap(double forearm, double desired, double blend, double expected)
    {
        var elbow = new SolPoint(0, 0);
        var rotation = SolHandPose.RotationDegrees(elbow, Wrist(elbow, forearm), 0, false, desired, blend);
        Assert.Equal(expected, rotation, 10);
    }

    [Fact]
    public void ForearmCrossingWrapDoesNotSpinRenderedHand()
    {
        var elbow = new SolPoint(0, 0);
        var before = SolHandPose.RotationDegrees(elbow, Wrist(elbow, 179.9), -80, true, 180, .5);
        var after = SolHandPose.RotationDegrees(elbow, Wrist(elbow, -179.9), -80, true, 180, .5);
        Assert.Equal(.1, Wrap(after - before), 10);
    }

    [Fact]
    public void BlendIsClampedAndWholeTurnsDoNotChangeDirection()
    {
        var elbow = new SolPoint(0, 0);
        var wrist = new SolPoint(0, 25);
        var below = SolHandPose.RotationDegrees(elbow, wrist, 103, false, 30, -10);
        var above = SolHandPose.RotationDegrees(elbow, wrist, 103, false, 30, 10);
        AssertDirection(90, below + 103);
        AssertDirection(30, above + 103);
        var baseRotation = SolHandPose.RotationDegrees(elbow, wrist, -80, true, 30, .7);
        var wrappedRotation = SolHandPose.RotationDegrees(elbow, wrist, -80 + 720, true, 30 - 1080, .7);
        AssertDirection(baseRotation, wrappedRotation);
    }

    [Fact]
    public void ExtremeFiniteInputsStillProduceFiniteRotation()
    {
        var rotation = SolHandPose.RotationDegrees(
            new SolPoint(-double.MaxValue, 0), new SolPoint(double.MaxValue, 0),
            double.MaxValue, true, -double.MaxValue, .5);
        Assert.True(double.IsFinite(rotation));
    }

    [Fact]
    public void CoincidentForearmAnchorsAreRejected()
    {
        var point = new SolPoint(10, 20);
        Assert.Throws<ArgumentException>(() => SolHandPose.RotationDegrees(point, point, -80, false, -90, 0));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteCoordinatesAnglesAndBlendAreRejected(double value)
    {
        var elbow = new SolPoint(0, 0);
        var wrist = new SolPoint(0, 25);
        foreach (var invalid in new[] { new SolPoint(value, 0), new SolPoint(0, value) })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => SolHandPose.RotationDegrees(invalid, wrist, -80, false, -90, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => SolHandPose.RotationDegrees(elbow, invalid, -80, false, -90, 0));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => SolHandPose.RotationDegrees(elbow, wrist, value, false, -90, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SolHandPose.RotationDegrees(elbow, wrist, -80, false, value, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SolHandPose.RotationDegrees(elbow, wrist, -80, false, -90, value));
    }

    private static SolPoint Wrist(SolPoint elbow, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new SolPoint(elbow.X + Math.Cos(radians) * 25, elbow.Y + Math.Sin(radians) * 25);
    }

    private static double TransformedFingerAxis(double sourceAxis, bool mirror, double rotation) =>
        (mirror ? 180 - sourceAxis : sourceAxis) + rotation;

    private static void AssertDirection(double expected, double actual) => Assert.Equal(0, Wrap(actual - expected), 9);

    private static double Wrap(double degrees)
    {
        var value = degrees % 360;
        if (value > 180) value -= 360;
        if (value < -180) value += 360;
        return value;
    }
}
