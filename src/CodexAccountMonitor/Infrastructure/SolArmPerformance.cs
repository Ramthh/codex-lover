namespace CodexAccountMonitor.Infrastructure;

public readonly record struct SolArmPose(SolLimb Limb, double FingerAxisDegrees, double OpenAmount);

/// <summary>
/// Authored shoulder, elbow and finger motion in screen coordinates. Joint angles
/// interpolate on explicit, unwrapped tracks, so a raised wrist never chooses a
/// different elbow side or a different shortest-arc branch during a transition.
/// </summary>
public static class SolArmPerformance
{
    public const double UpperLength = 17;
    public const double LowerLength = 22;

    public static SolArmPose Sample(SolPerformanceFrame f, bool left)
    {
        if (!double.IsFinite(f.Phase) || !double.IsFinite(f.Energy) ||
            !double.IsFinite(f.BodyX) || !double.IsFinite(f.BodyY) || !double.IsFinite(f.BodyAngle))
            throw new ArgumentOutOfRangeException(nameof(f), "Arm timing and body transform must be finite.");

        var energy = Math.Clamp(f.Energy, 0, 1);
        var phase = f.Phase % SolPerformance.ActDuration;
        if (phase < 0) phase += SolPerformance.ActDuration;
        var clock = phase - 2.1;
        var restUpper = left ? 110.0 : 70.0;
        var restForearm = left ? 125.0 : 55.0;
        var targetUpper = restUpper;
        var targetForearm = restForearm;
        var fingerFlex = 0.0;
        var open = 0.0;

        switch (f.Act)
        {
            case SolAct.Greet:
                if (!left)
                {
                    targetUpper = 25;
                    targetForearm = -80 + 8 * Math.Sin(clock * Math.PI * 2 * 1.18);
                    fingerFlex = 8 * Math.Sin(clock * Math.PI * 2 * 1.18 - .25);
                    open = energy;
                }
                break;
            case SolAct.Build:
                targetUpper = left ? 130 : 50;
                // Mirrored forearms have opposite vertical derivatives. The
                // same signed angular pulse therefore presses alternate hands.
                var typing = 2 * Math.Sin(clock * Math.PI * 2 / .62);
                targetForearm = (left ? 45 : 135) + typing;
                fingerFlex = (left ? 55 : 125) - targetForearm;
                break;
            case SolAct.Heart:
                targetUpper = left ? 145 : 35;
                targetForearm = left ? 40 : 140;
                fingerFlex = left ? -25 : 25;
                break;
            case SolAct.Celebrate:
                targetUpper = left ? 155 : 25;
                // Left explicitly travels 125 -> 250 through the outward side;
                // right travels 55 -> -70. Neither track wraps at +/-180.
                targetForearm = left ? 250 : -70;
                fingerFlex = 7 * Math.Sin(clock * 2.2) * (left ? -1 : 1);
                open = energy;
                break;
            case SolAct.Inspect:
                if (!left)
                {
                    targetUpper = 45;
                    targetForearm = -95;
                    fingerFlex = 25;
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(f), "Unknown Sol act.");
        }

        var upperAngle = restUpper + (targetUpper - restUpper) * energy;
        var forearmAngle = restForearm + (targetForearm - restForearm) * energy;
        var shoulder = new SolPoint(left ? 100 : 140, left ? 169 : 165);
        var elbow = Extend(shoulder, upperAngle, UpperLength);
        var wrist = Extend(elbow, forearmAngle, LowerLength);
        var limb = new SolLimb(WithBody(shoulder, f), WithBody(elbow, f), WithBody(wrist, f));
        // Finger direction is tied to the authored forearm, with bounded local
        // flexion. An absolute prop-facing angle cannot bend the wrist backward.
        var fingers = forearmAngle + Math.Clamp(fingerFlex, -30, 30) * energy + f.BodyAngle;
        return new(limb, fingers, open);
    }

    private static SolPoint Extend(SolPoint origin, double angle, double length)
    {
        var radians = angle * Math.PI / 180;
        return new(origin.X + Math.Cos(radians) * length, origin.Y + Math.Sin(radians) * length);
    }

    private static SolPoint WithBody(SolPoint point, SolPerformanceFrame f)
    {
        var rotated = SolKinematics.Rotate(point, new(120, 177), f.BodyAngle);
        return new(rotated.X + f.BodyX, rotated.Y + f.BodyY);
    }
}
