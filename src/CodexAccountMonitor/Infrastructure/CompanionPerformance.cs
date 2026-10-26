using CodexAccountMonitor.Core.Models;

namespace CodexAccountMonitor.Infrastructure;

public enum CompanionAct { Welcome, Read, Moonlight, Garden, Music, Breathe, Ready, Diagnose, Repair, Carry, Hurry, Reassure,
    StarGreet, Constellation, LightWeave, StarCatch, Orbit, Encore }

public readonly record struct CompanionFrame(
    CompanionAct Act, double Phase, double Energy,
    double BodyX, double BodyY, double BodyAngle, double HeadAngle,
    double LeftUpper, double LeftForearm, double LeftWrist,
    double RightUpper, double RightForearm, double RightWrist,
    double LeftStep, double RightStep, double PropScale);

/// <summary>Personality-specific authored angle tracks; lengths never interpolate or use IK branches.</summary>
public static class CompanionPerformance
{
    public const double Duration = 72;
    public static readonly IReadOnlyList<CompanionAct> LunaActs = Array.AsReadOnly(new[]
        { CompanionAct.Welcome, CompanionAct.Read, CompanionAct.Moonlight, CompanionAct.Garden, CompanionAct.Music, CompanionAct.Breathe });
    public static readonly IReadOnlyList<CompanionAct> TerraActs = Array.AsReadOnly(new[]
        { CompanionAct.Ready, CompanionAct.Diagnose, CompanionAct.Repair, CompanionAct.Carry, CompanionAct.Hurry, CompanionAct.Reassure });
    public static readonly IReadOnlyList<CompanionAct> AstraActs = Array.AsReadOnly(new[]
        { CompanionAct.StarGreet, CompanionAct.Constellation, CompanionAct.LightWeave, CompanionAct.StarCatch, CompanionAct.Orbit, CompanionAct.Encore });

    public static IReadOnlyList<CompanionAct> Acts(DesktopModel model) => model switch
    {
        DesktopModel.Luna => LunaActs, DesktopModel.Terra => TerraActs, DesktopModel.Astra => AstraActs,
        _ => throw new ArgumentOutOfRangeException(nameof(model))
    };

    public static CompanionFrame Sample(DesktopModel model, double seconds)
    {
        var acts = Acts(model);
        if (!double.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        var cycle = seconds % Duration; if (cycle < 0) cycle += Duration;
        if (cycle >= Duration) cycle = 0;
        var phase = cycle % 12;
        var energy = Ease((phase - 1) / 1.5) * (1 - Ease((phase - 10) / 2));
        var act = acts[(int)(cycle / 12)];
        var t = phase - 2.5;
        double x = 0, y = 0, body = 0, head = 0, lu = 0, lf = 0, lw = 0, ru = 0, rf = 0, rw = 0, ls = 0, rs = 0;
        var prop = energy;
        switch (act)
        {
            case CompanionAct.Welcome:
                lu = 25; lf = 140 + 6 * Math.Sin(t * 3.5); lw = 4 * Math.Sin(t * 3.5 - .2);
                head = -3 + Math.Sin(t * .9); body = -.7; prop = 0; break;
            case CompanionAct.Read:
                lu = 10; lf = -65 + 1.5 * Math.Sin(t * .7); ru = -10; rf = 60;
                lw = 9; rw = -8; head = 6 + Math.Sin(t * .65); y = .5; break;
            case CompanionAct.Moonlight:
                lf = -55; ru = -20; rf = -145 + 2 * Math.Sin(t); rw = 10;
                head = 3 * Math.Sin(t * .6); body = .8; break;
            case CompanionAct.Garden:
                lf = -35; ru = -25; rf = -75 + 3 * Math.Sin(t * 1.2); rw = 12;
                head = 5; body = 1.5; x = 1; break;
            case CompanionAct.Music:
                lu = 25; lf = 100 + 5 * Math.Sin(t * 2); ru = -20; rf = -105 - 5 * Math.Sin(t * 2);
                x = 1.7 * Math.Sin(t * 2); body = 2 * Math.Sin(t * 2); head = -3 * Math.Sin(t * 2 - .2);
                ls = 1.4 * Math.Max(0, Math.Sin(t * 2)); rs = 1.4 * Math.Max(0, -Math.Sin(t * 2)); break;
            case CompanionAct.Breathe:
                lf = -48; rf = 48; y = -.7 * Math.Sin(t * 1.15); head = -1.5;
                break;
            case CompanionAct.Ready:
                lf = -40; head = -4 + 1.2 * Math.Sin(t * 3); body = -1;
                rs = 2.5 * Math.Pow(Math.Max(0, Math.Sin(t * 4.2)), 2); prop = 0; break;
            case CompanionAct.Diagnose:
                lu = 8; lf = -65; ru = -15; rf = 75 + 2 * Math.Sin(t * 4); rw = -12;
                head = 5; y = .5; break;
            case CompanionAct.Repair:
                lu = 8; lf = -60; ru = -15; rf = -55 + 5 * Math.Sin(t * 3.2); rw = 8 * Math.Sin(t * 3.2);
                head = 3; x = 1; body = 1.3; break;
            case CompanionAct.Carry:
                lu = 5; lf = -60; ru = -5; rf = 65; head = -1.5;
                y = -.7 * Math.Sin(t * 3); ls = 1.5 * Math.Max(0, Math.Sin(t * 3)); rs = 1.5 * Math.Max(0, -Math.Sin(t * 3)); break;
            case CompanionAct.Hurry:
                lu = 8 * Math.Sin(t * 5); ru = -8 * Math.Sin(t * 5);
                lf = -12 + 12 * Math.Sin(t * 5); rf = 12 + 12 * Math.Sin(t * 5);
                ls = 3.5 * Math.Pow(Math.Max(0, Math.Sin(t * 5)), 2);
                rs = 3.5 * Math.Pow(Math.Max(0, -Math.Sin(t * 5)), 2);
                y = -.6 * Math.Abs(Math.Sin(t * 5)); body = -1.7; head = 3 * Math.Sin(t * .8); prop = 0; break;
            case CompanionAct.Reassure:
                lf = -70; ru = -20; rf = -125; rw = 8; head = -3 + 1.3 * Math.Sin(t * 1.8); break;
            case CompanionAct.StarGreet:
                lu = 23; lf = 133 + 7 * Math.Sin(t * 3); lw = 5 * Math.Sin(t * 3);
                head = -3 + 1.5 * Math.Sin(t); body = -1; break;
            case CompanionAct.Constellation:
                lu = 6; lf = -65; ru = -6; rf = 65; lw = 5; rw = -5;
                head = 4 + 1.5 * Math.Sin(t * .7); y = .5; break;
            case CompanionAct.LightWeave:
                lu = 20; lf = 100 + 10 * Math.Sin(t * 1.5); ru = -20; rf = -110 - 10 * Math.Sin(t * 1.5);
                lw = 8 * Math.Sin(t * 1.5); rw = -lw; head = 2 * Math.Sin(t * .8);
                body = 1.6 * Math.Sin(t * 1.5); x = 1.2 * Math.Sin(t * 1.5); break;
            case CompanionAct.StarCatch:
                lf = -35; ru = -22; rf = -136 + 7 * Math.Sin(t * 1.8); rw = 7;
                head = 3 * Math.Sin(t * 1.1); body = .8; break;
            case CompanionAct.Orbit:
                lf = -55 + 5 * Math.Sin(t * 1.4); rf = 55 - 5 * Math.Sin(t * 1.4);
                head = -2 + 1.2 * Math.Sin(t * .8); body = .8 * Math.Sin(t * 1.4); break;
            case CompanionAct.Encore:
                lu = 25; lf = 110 + 6 * Math.Sin(t * 2); ru = -25; rf = -120 - 6 * Math.Sin(t * 2);
                x = 1.5 * Math.Sin(t * 2); body = 2 * Math.Sin(t * 2); head = -2 * Math.Sin(t * 2);
                ls = 1.5 * Math.Max(0, Math.Sin(t * 2)); rs = 1.5 * Math.Max(0, -Math.Sin(t * 2)); break;
        }
        return new(act, phase, energy, x * energy, y * energy, body * energy, head * energy,
            lu * energy, lf * energy, lw * energy, ru * energy, rf * energy, rw * energy,
            ls * energy, rs * energy, prop);
    }

    public static double Ease(double value)
    {
        var t = Math.Clamp(value, 0, 1);
        return t * t * t * (10 + t * (-15 + 6 * t));
    }
}
