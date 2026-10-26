namespace CodexAccountMonitor.Infrastructure;

public enum SolRoutine { Wave, Build, Heart, Celebrate, Peek }

/// <summary>
/// Joint angles are degrees relative to the neutral rig. Crouch, Jump and PropY
/// are fractions of model height (positive Crouch/PropY down, positive Jump up).
/// Turn is signed normalized yaw, EyeScale is vertical eye scale, and the prop
/// and particle values are normalized. No clock, UI, random state or I/O is used.
/// </summary>
public readonly record struct SolMotionFrame(
    SolRoutine Routine,
    double HeadAngle,
    double BodyAngle,
    double LeftArmAngle,
    double RightArmAngle,
    double RightWristAngle,
    double LeftLegAngle,
    double RightLegAngle,
    double Crouch,
    double Jump,
    double Turn,
    double EyeScale,
    double PropScale,
    double PropY,
    double PropAngle,
    double ParticleProgress);

/// <summary>A seamless five-routine performance with anticipation, prop entry and recovery.</summary>
public static class SolMotionTimeline
{
    public const double Duration = 45.0;
    public const double RoutineDuration = 9.0;
    public static IReadOnlyList<string> RoutineNames { get; } = Array.AsReadOnly(new[]
        { "Wave", "Build", "Heart", "Celebrate", "Peek" });

    /// <summary>Samples a signed time; negative finite times wrap around the same loop.</summary>
    public static SolMotionFrame Sample(double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds))
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds), "Animation time must be finite.");
        var cycle = elapsedSeconds % Duration;
        if (cycle < 0) cycle += Duration;
        // Floating-point addition for very small negative times can round to Duration.
        if (cycle >= Duration) cycle = 0;
        var routine = (SolRoutine)(int)(cycle / RoutineDuration);
        var t = cycle % RoutineDuration;
        var entry = Smooth((t - .65) / 1.05);
        var exit = 1 - Smooth((t - 7.05) / 1.55);
        var active = entry * exit;
        var anticipation = Pulse(t, .44, .43);
        var blink = Math.Max(Pulse(t, 2.36, .115), Pulse(t, 5.82, .105));
        var eye = 1 - .92 * blink;
        var prop = Pop((t - .85) / .7) * exit;
        var timeActive = Math.Max(0, t - 1.7);

        // Every routine starts and ends at the same neutral pose. Its small preparation
        // dip begins before the main action, so props and large motions feel connected.
        var head = -3 * anticipation;
        var body = 2.5 * anticipation;
        var leftArm = 0.0;
        var rightArm = 0.0;
        var rightWrist = 0.0;
        var leftLeg = 0.0;
        var rightLeg = 0.0;
        var crouch = .025 * anticipation;
        var jump = 0.0;
        var turn = 0.0;
        var propScale = 0.0;
        var propY = 0.0;
        var propAngle = 0.0;
        var particles = 0.0;

        switch (routine)
        {
            case SolRoutine.Wave:
                var wave = Math.Sin(timeActive * Math.PI * 2 * 1.65);
                head += active * (7 + 3 * Math.Sin(timeActive * 2.1));
                body += active * -4;
                leftArm = active * (-13 + 4 * Math.Sin(timeActive * 2.1));
                rightArm = active * (5 + 12 * wave);
                rightWrist = active * 18 * wave;
                leftLeg = active * -3;
                rightLeg = active * 4;
                turn = active * -.07;
                break;

            case SolRoutine.Build:
                var typing = Math.Sin(timeActive * Math.PI * 2 * 3.5);
                head += active * (13 + 2 * Math.Sin(timeActive * 3));
                body += active * 7;
                leftArm = active * (-55 + 7 * typing);
                rightArm = active * (90 - 7 * typing);
                leftLeg = active * -7;
                rightLeg = active * 7;
                crouch += active * .045;
                propScale = prop;
                propY = active * (.13 * (1 - entry) - .035);
                propAngle = active * (-4 + .9 * Math.Sin(timeActive * 2));
                break;

            case SolRoutine.Heart:
                var present = Smooth((t - 2.2) / 1.0);
                var pulse = Math.Sin(timeActive * Math.PI * 2 * .85);
                head += active * (-6 + 3 * Math.Sin(timeActive * 1.2));
                body += active * -3;
                leftArm = active * (-36 - 12 * present);
                rightArm = active * (60 - 10 * present);
                leftLeg = active * -3;
                rightLeg = active * 3;
                propScale = prop * (1 + .035 * pulse * entry);
                propY = active * (.075 - .14 * present);
                propAngle = active * 5 * Math.Sin(timeActive * 1.4);
                turn = active * .04;
                break;

            case SolRoutine.Celebrate:
                var firstHop = Hop(t, 2.05, .74);
                var secondHop = Hop(t, 3.7, .84);
                var thirdHop = Hop(t, 5.5, .98);
                var airborne = Math.Max(firstHop, Math.Max(secondHop, thirdHop));
                var preparation = Math.Max(Pulse(t, 1.91, .18), Math.Max(Pulse(t, 3.55, .2), Pulse(t, 5.33, .22)));
                head += active * (-8 + 5 * Math.Sin(timeActive * 2));
                body += active * (5 * Math.Sin(timeActive * 2.3));
                leftArm = active * (88 + 18 * airborne);
                rightArm = active * (-45 - 20 * airborne);
                leftLeg = active * (-7 - 17 * firstHop + 13 * secondHop - 20 * thirdHop);
                rightLeg = active * (7 + 13 * firstHop - 17 * secondHop + 20 * thirdHop);
                crouch += active * .052 * preparation;
                jump = .125 * airborne;
                propScale = prop * (1 + .065 * airborne);
                propY = active * (-.18 - .065 * airborne);
                propAngle = active * (12 * Math.Sin(timeActive * 3));
                particles = active * Math.Clamp((t - 2.05) / 4.7, 0, 1);
                break;

            case SolRoutine.Peek:
                var look = Math.Sin(timeActive * 1.4);
                head += active * (-14 + 12 * look);
                body += active * (-5 + 3 * look);
                leftArm = active * -18;
                rightArm = active * (-15 + 9 * look);
                leftLeg = active * -7;
                rightLeg = active * 5;
                crouch += active * .012;
                turn = active * (.25 * look);
                propScale = prop;
                propY = active * (-.17 + .012 * Math.Sin(timeActive * 2));
                propAngle = active * (-12 + 10 * look);
                break;
        }

        return new(routine, head, body, leftArm, rightArm, rightWrist, leftLeg, rightLeg,
            crouch, jump, turn, eye, propScale, propY, propAngle, particles);
    }

    private static double Smooth(double value)
    {
        var t = Math.Clamp(value, 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static double Pulse(double time, double center, double radius) =>
        Smooth(1 - Math.Abs(time - center) / radius);

    private static double Hop(double time, double start, double duration)
    {
        var t = (time - start) / duration;
        return t > 0 && t < 1 ? Math.Sin(t * Math.PI) : 0;
    }

    private static double Pop(double value)
    {
        var t = Math.Clamp(value, 0, 1) - 1;
        const double overshoot = 1.55;
        return 1 + (overshoot + 1) * t * t * t + overshoot * t * t;
    }
}
