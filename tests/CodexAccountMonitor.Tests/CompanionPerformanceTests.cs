using CodexAccountMonitor.Core.Models;
using CodexAccountMonitor.Infrastructure;

namespace CodexAccountMonitor.Tests;

public sealed class CompanionPerformanceTests
{
    [Theory]
    [InlineData(DesktopModel.Luna)]
    [InlineData(DesktopModel.Terra)]
    [InlineData(DesktopModel.Astra)]
    public void FullLoopKeepsFiniteContinuousTracksAndNaturalWristAndFootRanges(DesktopModel model)
    {
        var previous = CompanionPerformance.Sample(model, -1 / 120d);
        var acts = new HashSet<CompanionAct>();
        for (var i = 0; i <= 72 * 120; i++)
        {
            var frame = CompanionPerformance.Sample(model, i / 120d);
            acts.Add(frame.Act);
            var values = Tracks(frame); var last = Tracks(previous);
            for (var j = 0; j < values.Length; j++)
            {
                Assert.True(double.IsFinite(values[j]));
                // A degree track jumping across +/-180 would fail by hundreds of degrees.
                Assert.InRange(Math.Abs(values[j] - last[j]), 0, 2);
            }
            Assert.InRange(frame.Energy, 0, 1);
            Assert.InRange(frame.PropScale, 0, 1);
            Assert.InRange(frame.LeftWrist, -15, 15); Assert.InRange(frame.RightWrist, -15, 15);
            Assert.InRange(frame.LeftStep, 0, 3.5); Assert.InRange(frame.RightStep, 0, 3.5);
            // Upper arms remain below shoulder level throughout the routines.
            Assert.InRange(frame.LeftUpper, -10, 30); Assert.InRange(frame.RightUpper, -30, 10);
            previous = frame;
        }
        Assert.Equal(6, acts.Count);
        Assert.Equal(CompanionPerformance.Acts(model).OrderBy(x => x), acts.OrderBy(x => x));
        foreach (var other in new[] { DesktopModel.Luna, DesktopModel.Terra, DesktopModel.Astra }.Where(other => other != model))
            Assert.DoesNotContain(acts, act => CompanionPerformance.Acts(other).Contains(act));
    }

    [Theory]
    [InlineData(DesktopModel.Luna)]
    [InlineData(DesktopModel.Terra)]
    [InlineData(DesktopModel.Astra)]
    public void ActsReturnToSharedRestWithoutLimbOrPropPopping(DesktopModel model)
    {
        for (var t = 0; t <= 72; t += 12)
        {
            var rest = CompanionPerformance.Sample(model, t);
            Assert.All(Tracks(rest), value => Assert.Equal(0, value));
            Assert.Equal(0, rest.PropScale);
            foreach (var nearby in new[] { t - .001, t + .001 })
                Assert.All(Tracks(CompanionPerformance.Sample(model, nearby)), value => Assert.InRange(Math.Abs(value), 0, .0001));
        }
        Assert.Equal(CompanionPerformance.Sample(model, 71.5), CompanionPerformance.Sample(model, -.5));
        Assert.Equal(CompanionPerformance.Sample(model, 15.5), CompanionPerformance.Sample(model, 87.5));
    }

    [Fact]
    public void InvalidInputsCannotCreateCorruptFrames()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CompanionPerformance.Sample(DesktopModel.Sol, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CompanionPerformance.Sample((DesktopModel)42, 0));
        foreach (var time in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => CompanionPerformance.Sample(DesktopModel.Luna, time));
    }

    private static double[] Tracks(CompanionFrame f) =>
        [f.BodyX, f.BodyY, f.BodyAngle, f.HeadAngle, f.LeftUpper, f.LeftForearm, f.LeftWrist,
            f.RightUpper, f.RightForearm, f.RightWrist, f.LeftStep, f.RightStep];
}
