using CodexAccountMonitor.Infrastructure;

namespace CodexAccountMonitor.Tests;

public sealed class SolKinematicsTests
{
    [Theory]
    [InlineData(47, 48, 30, 24, 1)]
    [InlineData(-20, 22, 24, 30, -1)]
    [InlineData(12, -20, 30, 24, 1)]
    [InlineData(12, 56, 30, 24, -1)]
    public void ReachableTargetsPreserveBothLengthsAndExactEndpoint(double x, double y, double upper, double lower, int bend)
    {
        var root = new SolPoint(12, 18);
        var target = new SolPoint(x, y);
        var limb = SolKinematics.Solve(root, target, upper, lower, bend);
        Assert.Equal(root, limb.Root);
        Assert.Equal(target, limb.End);
        AssertBoneLengths(limb, upper, lower);
    }

    [Fact]
    public void DistantTargetClampsToFullyExtendedLimb()
    {
        var limb = SolKinematics.Solve(new SolPoint(10, 10), new SolPoint(110, 10), 24, 16, 1);
        Assert.Equal(new SolPoint(34, 10), limb.Joint);
        Assert.Equal(new SolPoint(50, 10), limb.End);
        AssertBoneLengths(limb, 24, 16);
    }

    [Theory]
    [InlineData(30, 12, 30)]
    [InlineData(12, 30, -12)]
    public void TooCloseTargetClampsToFoldedLimb(double upper, double lower, double expectedJointX)
    {
        var limb = SolKinematics.Solve(new SolPoint(0, 0), new SolPoint(2, 0), upper, lower, -1);
        Assert.Equal(new SolPoint(expectedJointX, 0), limb.Joint);
        Assert.Equal(new SolPoint(18, 0), limb.End);
        AssertBoneLengths(limb, upper, lower);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void CoincidentEqualLengthAnchorsFoldOnDeterministicBendSide(int bend)
    {
        var root = new SolPoint(9, 11);
        var limb = SolKinematics.Solve(root, root, 20, 20, bend);
        Assert.Equal(root, limb.End);
        Assert.Equal(new SolPoint(9, 11 + 20 * bend), limb.Joint);
        AssertBoneLengths(limb, 20, 20);
    }

    [Theory]
    [InlineData(30, 12, 30)]
    [InlineData(12, 30, -12)]
    public void CoincidentUnequalLengthAnchorsChooseFiniteMinimumReach(double upper, double lower, double expectedJointX)
    {
        var limb = SolKinematics.Solve(new SolPoint(0, 0), new SolPoint(0, 0), upper, lower, 1);
        Assert.Equal(new SolPoint(expectedJointX, 0), limb.Joint);
        Assert.Equal(new SolPoint(18, 0), limb.End);
        AssertBoneLengths(limb, upper, lower);
    }

    [Fact]
    public void NearlyFoldedEqualBonesRemainFinite()
    {
        var limb = SolKinematics.Solve(new SolPoint(0, 0), new SolPoint(1e-200, 0), 20, 20, 1);
        Assert.Equal(new SolPoint(1e-200, 0), limb.End);
        AssertBoneLengths(limb, 20, 20);
    }

    [Fact]
    public void BendDirectionsMirrorJointAcrossTargetAxis()
    {
        var root = new SolPoint(4, 7);
        var target = new SolPoint(34, 47);
        var positive = SolKinematics.Solve(root, target, 36, 29, 1);
        var negative = SolKinematics.Solve(root, target, 36, 29, -1);
        var positiveAlong = (positive.Joint.X - root.X) * .6 + (positive.Joint.Y - root.Y) * .8;
        var negativeAlong = (negative.Joint.X - root.X) * .6 + (negative.Joint.Y - root.Y) * .8;
        var positiveAcross = -(positive.Joint.X - root.X) * .8 + (positive.Joint.Y - root.Y) * .6;
        var negativeAcross = -(negative.Joint.X - root.X) * .8 + (negative.Joint.Y - root.Y) * .6;
        Assert.Equal(positiveAlong, negativeAlong, 10);
        Assert.Equal(positiveAcross, -negativeAcross, 10);
        Assert.True(positiveAcross > 0);
        Assert.Equal(positive.End, negative.End);
        AssertBoneLengths(positive, 36, 29);
        AssertBoneLengths(negative, 36, 29);
    }

    [Fact]
    public void EntireReachRangePreservesConnectedBoneLengths()
    {
        foreach (var bend in new[] { -1, 1 })
        foreach (var angle in new[] { -179.0, -90, -17, 0, 33, 90, 170 })
        for (var radius = 0; radius <= 90; radius++)
        {
            var root = new SolPoint(110, 75);
            var target = SolKinematics.Rotate(new SolPoint(root.X + radius, root.Y), root, angle);
            var limb = SolKinematics.Solve(root, target, 30, 24, bend);
            AssertBoneLengths(limb, 30, 24);
            Assert.Equal(Math.Clamp(radius, 6, 54), Distance(root, limb.End), 8);
        }
    }

    [Fact]
    public void OverflowingTargetDistanceStillProducesFiniteClampedLimb()
    {
        var root = new SolPoint(-double.MaxValue, 0);
        var target = new SolPoint(double.MaxValue, 0);
        var limb = SolKinematics.Solve(root, target, 4e307, 4e307, 1);
        Assert.True(double.IsFinite(limb.Joint.X));
        Assert.True(double.IsFinite(limb.End.X));
        Assert.Equal(0, limb.Joint.Y);
        Assert.Equal(0, limb.End.Y);
        Assert.Equal(4e307, limb.Joint.X - root.X, 1e293);
        Assert.Equal(4e307, limb.End.X - limb.Joint.X, 1e293);
    }

    [Theory]
    [InlineData(90, 10, 25)]
    [InlineData(-90, 10, 15)]
    [InlineData(180, 5, 20)]
    [InlineData(450, 10, 25)]
    public void RotationUsesPivotAndClockwiseScreenAngles(double angle, double x, double y)
    {
        var point = SolKinematics.Rotate(new SolPoint(15, 20), new SolPoint(10, 20), angle);
        Assert.Equal(x, point.X, 10);
        Assert.Equal(y, point.Y, 10);
    }

    [Fact]
    public void RotationPreservesRadiusAndCanBeReversed()
    {
        var original = new SolPoint(27, -9);
        var pivot = new SolPoint(4, 12);
        var rotated = SolKinematics.Rotate(original, pivot, 23.5);
        var restored = SolKinematics.Rotate(rotated, pivot, -23.5);
        Assert.Equal(Distance(original, pivot), Distance(rotated, pivot), 10);
        Assert.Equal(original.X, restored.X, 10);
        Assert.Equal(original.Y, restored.Y, 10);
        Assert.Equal(original, SolKinematics.Rotate(original, pivot, 360));
        Assert.True(double.IsFinite(SolKinematics.Rotate(original, pivot, double.MaxValue).X));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteCoordinatesAndAnglesAreRejected(double value)
    {
        var valid = new SolPoint(0, 0);
        foreach (var invalid in new[] { new SolPoint(value, 0), new SolPoint(0, value) })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => SolKinematics.Solve(invalid, valid, 20, 20, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => SolKinematics.Solve(valid, invalid, 20, 20, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => SolKinematics.Rotate(invalid, valid, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => SolKinematics.Rotate(valid, invalid, 0));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => SolKinematics.Rotate(valid, valid, value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidBoneLengthsAreRejected(double value)
    {
        var root = new SolPoint(0, 0);
        var target = new SolPoint(20, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => SolKinematics.Solve(root, target, value, 20, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => SolKinematics.Solve(root, target, 20, value, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-2)]
    public void InvalidBendDirectionIsRejected(int direction) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SolKinematics.Solve(new SolPoint(0, 0), new SolPoint(20, 0), 20, 20, direction));

    [Fact]
    public void UnrepresentableGeometryThrowsRatherThanReturningNonFinitePoints()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SolKinematics.Solve(new SolPoint(0, 0), new SolPoint(20, 0), double.MaxValue, double.MaxValue, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => SolKinematics.Solve(new SolPoint(double.MaxValue, double.MaxValue), new SolPoint(double.MaxValue, double.MaxValue), double.MaxValue / 4, double.MaxValue / 4, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => SolKinematics.Rotate(new SolPoint(double.MaxValue, 0), new SolPoint(-double.MaxValue, 0), 90));
    }

    private static void AssertBoneLengths(SolLimb limb, double upper, double lower)
    {
        Assert.All(new[] { limb.Root.X, limb.Root.Y, limb.Joint.X, limb.Joint.Y, limb.End.X, limb.End.Y }, value => Assert.True(double.IsFinite(value)));
        Assert.Equal(upper, Distance(limb.Root, limb.Joint), 8);
        Assert.Equal(lower, Distance(limb.Joint, limb.End), 8);
    }

    private static double Distance(SolPoint a, SolPoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
