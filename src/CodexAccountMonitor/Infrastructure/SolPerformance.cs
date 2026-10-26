namespace CodexAccountMonitor.Infrastructure;

public enum SolAct { Greet, Build, Heart, Celebrate, Inspect }

public readonly record struct SolPoint(double X, double Y);

/// <summary>
/// Samples a 240×256 stage. Body/Head positions are offsets from the neutral rig;
/// hands, feet and prop are absolute stage points. Angles are clockwise degrees.
/// Gaze is normalized [-1,1]; Energy, EyeOpen, PropScale, FingerCurl and Landing
/// are normalized [0,1]. Positive screen Y is downward.
/// </summary>
public readonly record struct SolPerformanceFrame(
    SolAct Act, double Phase, double Energy,
    double BodyX, double BodyY, double BodyAngle,
    double HeadAngle, double HeadX, double HeadY,
    double GazeX, double GazeY, double EyeOpen,
    SolPoint LeftHand, SolPoint RightHand, SolPoint LeftFoot, SolPoint RightFoot,
    SolPoint Prop, double PropScale, double PropAngle, double FingerCurl, double Landing);

/// <summary>
/// Deterministic character performance for a connected two-link limb rig.
/// Quintic entrance/exit and jump curves have zero velocity and acceleration
/// at their endpoints. This type has no UI, clock, random state or I/O.
/// </summary>
public static class SolPerformance
{
    public const double Duration = 60.0;
    public const double ActDuration = 12.0;
    public static IReadOnlyList<string> ActNames { get; } = Array.AsReadOnly(new[]
        { "Greet", "Build", "Heart", "Celebrate", "Inspect" });

    private static readonly SolPoint RestLeftHand = new(85, 195);
    private static readonly SolPoint RestRightHand = new(154, 195);
    private static readonly SolPoint RestLeftFoot = new(104, 220);
    private static readonly SolPoint RestRightFoot = new(141, 220);
    private static readonly SolPoint RestProp = new(122, 200);

    /// <summary>Negative finite times wrap around the same seamless 60-second performance.</summary>
    public static SolPerformanceFrame Sample(double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds))
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds), "Performance time must be finite.");
        var cycle = elapsedSeconds % Duration;
        if (cycle < 0) cycle += Duration;
        if (cycle >= Duration) cycle = 0;
        var act = (SolAct)(int)(cycle / ActDuration);
        var phase = cycle % ActDuration;
        var enter = Ease((phase - 1) / 1.1);
        var leave = 1 - Ease((phase - 10) / 2);
        var energy = enter * leave;
        var headEnergy = Ease((phase - 1.14) / 1.1) * (1 - Ease((phase - 10.14) / 1.86));
        var prepare = Pulse(phase, .55, .55);
        // Let secondary motion run through the entrance instead of starting its
        // oscillator abruptly when the main pose reaches full energy.
        var clock = phase - 2.1;
        var propScale = Ease((phase - 1.1) / .95) * leave;

        var bodyX = 0.0;
        var bodyY = 1.8 * prepare;
        var bodyAngle = -.7 * prepare;
        var headAngle = 1.2 * prepare;
        var headX = 0.0;
        var headY = .5 * prepare;
        var gazeX = 0.0;
        var gazeY = 0.0;
        var leftHand = RestLeftHand;
        var rightHand = RestRightHand;
        var leftFoot = RestLeftFoot;
        var rightFoot = RestRightFoot;
        var prop = RestProp;
        var propAngle = 0.0;
        var fingers = 0.0;
        var landing = 0.0;
        var blink = Math.Max(Pulse(phase, 3.23, .13), Pulse(phase, 8.34, .12));
        var eyeOpen = 1 - .97 * blink;

        switch (act)
        {
            case SolAct.Greet:
                var wave = Math.Sin(clock * Math.PI * 2 * 1.18);
                bodyX = energy * (1.2 + .45 * Math.Sin(clock * 1.1));
                bodyY += energy * .55 * Math.Sin(clock * 1.4);
                bodyAngle += energy * -1.7;
                headAngle += headEnergy * (4.5 + 1.4 * Math.Sin((clock - .12) * 1.1));
                headX = headEnergy * .9;
                headY += headEnergy * -.45;
                gazeX = energy * (.12 + .08 * Math.Sin(clock * .8));
                gazeY = energy * -.06;
                fingers = energy * (.08 + .065 * (1 + wave));
                propScale = 0;
                break;

            case SolAct.Build:
                var keyLeft = PulseCycle(clock, .62, 0);
                var keyRight = PulseCycle(clock, .62, .31);
                bodyX = energy * -.7;
                bodyY += energy * (2.4 + .18 * Math.Sin(clock * 1.1));
                bodyAngle += energy * 1.8;
                headAngle += headEnergy * (7 + .65 * Math.Sin((clock - .12) * 1.5));
                headX = headEnergy * -.8;
                headY += headEnergy * 1.25;
                gazeX = energy * .16 * Math.Sin(clock * .95);
                gazeY = energy * .62;
                // The original fingers project past their wrist anchors. Keep
                // wrists above the deck so fingertip presses reach the keys.
                prop = Mix(RestProp, new(122, 202), energy);
                propAngle = energy * -.8;
                fingers = energy * (.48 + .12 * Math.Max(keyLeft, keyRight));
                break;

            case SolAct.Heart:
                var offer = Ease((phase - 3) / 1.4);
                var heartBreath = Math.Sin(clock * 1.6);
                bodyX = energy * .4;
                bodyY += energy * (-.3 - .3 * heartBreath);
                bodyAngle += energy * -.7;
                headAngle += headEnergy * (-4.2 + .8 * Math.Sin((clock - .15) * 1.15));
                headX = headEnergy * -.55;
                headY += headEnergy * -.3;
                gazeX = energy * .02;
                gazeY = energy * (.25 * (1 - offer) - .03 * offer);
                prop = Mix(RestProp, new(122.5, 188 - 5 * offer - .3 * heartBreath), energy);
                propAngle = energy * 1.2 * Math.Sin(clock * 1.15);
                fingers = energy * .62;
                eyeOpen *= 1 - .10 * headEnergy;
                break;

            case SolAct.Celebrate:
                var firstHop = Bell(phase, 3.0, 1.15);
                var secondHop = Bell(phase, 6.2, 1.25);
                var flight = Math.Max(firstHop, secondHop);
                var coil = Math.Max(Pulse(phase, 2.80, .24), Pulse(phase, 5.98, .26));
                landing = Math.Max(Pulse(phase, 4.30, .26), Pulse(phase, 7.60, .27)) * energy;
                var jump = 15 * flight;
                bodyX = energy * .55 * Math.Sin(clock * 1.1);
                bodyY += energy * (1.8 * coil + 2.2 * landing) - jump;
                bodyAngle += energy * (1.2 * Math.Sin(clock * 1.4));
                headAngle += headEnergy * (-3 + 1.4 * Math.Sin((clock - .14) * 1.4));
                headY += headEnergy * -.55;
                gazeY = energy * -.16;
                // Ankles follow the same vertical flight path as the pelvis. They
                // remain planted during preparation/landing instead of sliding down.
                leftFoot = new(RestLeftFoot.X - 2.3 * flight, RestLeftFoot.Y - jump);
                rightFoot = new(RestRightFoot.X + 2.3 * flight, RestRightFoot.Y - jump);
                prop = Mix(RestProp, new(193, 221), energy);
                propAngle = energy * 7 * Math.Sin(clock * 1.2);
                fingers = energy * .12;
                break;

            case SolAct.Inspect:
                var scan = Math.Sin(clock * .85);
                bodyX = energy * (2.3 + .5 * scan);
                bodyY += energy * .65;
                bodyAngle += energy * 1.0;
                headAngle += headEnergy * (-2.5 + 3.5 * Math.Sin((clock - .16) * .85));
                headX = headEnergy * (1.6 + .35 * scan);
                headY += headEnergy * -.4;
                gazeX = energy * (.57 + .14 * scan);
                gazeY = energy * -.10;
                prop = Mix(RestProp, new(167 + 1.6 * scan, 134 + .8 * Math.Cos(clock * .85)), energy);
                propAngle = energy * (-12 + 4 * scan);
                fingers = energy * .7;
                break;
        }

        var frame = new SolPerformanceFrame(act, phase, energy, bodyX, bodyY, bodyAngle,
            headAngle, headX, headY, gazeX, gazeY, eyeOpen, default, default,
            leftFoot, rightFoot, prop, propScale, propAngle, fingers, landing);
        var leftPose = SolArmPerformance.Sample(frame, true);
        var rightPose = SolArmPerformance.Sample(frame, false);
        // Props follow the authored hands. Never force an elbow to the wrong
        // side merely to reach an independently animated prop target.
        if (act == SolAct.Heart)
            prop = Mix(RestProp, new((leftPose.Limb.End.X + rightPose.Limb.End.X) / 2,
                (leftPose.Limb.End.Y + rightPose.Limb.End.Y) / 2 - 4), energy);
        else if (act == SolAct.Inspect)
        {
            var fingerRadians = rightPose.FingerAxisDegrees * Math.PI / 180;
            var grip = new SolPoint(rightPose.Limb.End.X + 8 * Math.Cos(fingerRadians),
                rightPose.Limb.End.Y + 8 * Math.Sin(fingerRadians));
            var handle = SolKinematics.Rotate(new(-6, 17), new(0, 0), propAngle);
            prop = Mix(RestProp, new(grip.X - handle.X, grip.Y - handle.Y), energy);
        }
        return frame with { LeftHand = leftPose.Limb.End, RightHand = rightPose.Limb.End, Prop = prop };
    }
    private static SolPoint Mix(SolPoint from, SolPoint to, double amount) =>
        new(from.X + (to.X - from.X) * amount, from.Y + (to.Y - from.Y) * amount);

    private static double Ease(double value)
    {
        var t = Math.Clamp(value, 0, 1);
        return Math.Clamp(t * t * t * (t * (t * 6 - 15) + 10), 0, 1);
    }

    private static double Pulse(double time, double center, double halfWidth) =>
        Ease(1 - Math.Abs(time - center) / halfWidth);

    private static double PulseCycle(double time, double duration, double offset)
    {
        var cycle = (time + offset) % duration;
        if (cycle < 0) cycle += duration;
        return Bell(cycle, 0, duration);
    }

    private static double Bell(double time, double start, double duration)
    {
        var t = (time - start) / duration;
        if (t <= 0 || t >= 1) return 0;
        return t < .5 ? Ease(t * 2) : 1 - Ease((t - .5) * 2);
    }
}
