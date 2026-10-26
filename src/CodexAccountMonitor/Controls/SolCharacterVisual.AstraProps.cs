using System.Windows;
using System.Windows.Media;
using CodexAccountMonitor.Infrastructure;

namespace CodexAccountMonitor.Controls;

public sealed partial class SolCharacterVisual
{
    private static Brush GoldLight => Solid("#FFE3A0");
    private static Brush GoldGlow => Radial("#ADFFC75C", "#00FFC75C");

    private static void GoldStar(DrawingContext dc, Point p, double size, double angle)
    {
        dc.DrawEllipse(GoldGlow, null, p, size * 2.7, size * 2.7);
        Star(dc, p, size, angle, GoldLight);
    }

    private void DrawAstraProps(DrawingContext dc, CompanionFrame f, SolLimb left, SolLimb right)
    {
        var middle = HandsCenter(left, right);
        dc.PushOpacity(f.PropScale);
        switch (f.Act)
        {
            case CompanionAct.StarGreet:
                for (var i = 0; i < 3; i++)
                {
                    var t = (f.Phase * .35 + i / 3d) % 1;
                    dc.PushOpacity(Math.Sin(t * Math.PI));
                    GoldStar(dc, new Point(left.End.X - 8 - t * 12, left.End.Y - 10 - t * 15), 2 + t, t * 65);
                    dc.Pop();
                }
                break;
            case CompanionAct.Constellation:
                At(dc, new Point(middle.X, middle.Y - 22), f.PropScale, 0, () =>
                {
                    Point[] points = [new(-22, 4), new(-13, -12), new(0, -3), new(13, -17), new(24, 0), new(6, 10)];
                    var drawTo = CompanionPerformance.Ease((f.Phase - 2.5) / 3) * (points.Length - 1);
                    for (var i = 1; i < points.Length; i++)
                    {
                        var progress = Math.Clamp(drawTo - i + 1, 0, 1);
                        if (progress > 0) dc.DrawLine(new Pen(Solid("#BFF5CD76"), .8), points[i - 1], points[i - 1] + (points[i] - points[i - 1]) * progress);
                    }
                    for (var i = 0; i < points.Length; i++)
                    {
                        dc.PushOpacity(.45 + .5 * Math.Clamp(drawTo - i + 1, 0, 1));
                        GoldStar(dc, points[i], 2.1 + .35 * Math.Sin(f.Phase * 2 + i), i * 22); dc.Pop();
                    }
                });
                break;
            case CompanionAct.LightWeave:
                var ribbon = new StreamGeometry();
                using (var curve = ribbon.Open())
                {
                    curve.BeginFigure(P(left.End), false, false);
                    curve.BezierTo(new Point(middle.X - 12, middle.Y - 25 - 5 * Math.Sin(f.Phase)),
                        new Point(middle.X + 12, middle.Y + 25 + 5 * Math.Sin(f.Phase)), P(right.End), true, false);
                }
                ribbon.Freeze();
                dc.DrawGeometry(null, RoundPen(Solid("#44FFD681"), 5), ribbon);
                dc.DrawGeometry(null, RoundPen(GoldLight, 1.2), ribbon);
                for (var i = 0; i < 3; i++)
                {
                    var t = (f.Phase * .28 + i / 3d) % 1;
                    GoldStar(dc, new Point(left.End.X + (right.End.X - left.End.X) * t,
                        left.End.Y + (right.End.Y - left.End.Y) * t + Math.Sin(t * Math.PI * 2) * -8), 2, t * 90);
                }
                break;
            case CompanionAct.StarCatch:
                var catchAt = new Point(right.End.X + 2, right.End.Y - 9);
                var fall = (f.Phase * .32) % 1;
                GoldStar(dc, new Point(catchAt.X - 6 * Math.Sin(fall * Math.PI), catchAt.Y - 27 * (1 - fall)), 4, fall * 95);
                dc.DrawEllipse(GoldGlow, null, catchAt, 9 + 5 * fall, 5 + 4 * fall);
                break;
            case CompanionAct.Orbit:
                var sun = new Point(middle.X, middle.Y - 10);
                dc.DrawEllipse(GoldGlow, null, sun, 17, 17);
                dc.DrawEllipse(Gradient("#D99425", "#FFF4CC", "#FFBE4B"), new Pen(GoldLight, .6), sun, 5, 5);
                dc.DrawEllipse(null, new Pen(Solid("#80E3B766"), .7), sun, 22, 9);
                for (var i = 0; i < 3; i++)
                {
                    var angle = f.Phase * 1.1 + i * Math.PI * 2 / 3;
                    GoldStar(dc, new Point(sun.X + Math.Cos(angle) * 22, sun.Y + Math.Sin(angle) * 9), 2.2, angle * 30);
                }
                break;
            case CompanionAct.Encore:
                for (var i = 0; i < 7; i++)
                {
                    var t = (f.Phase * .25 + i / 7d) % 1;
                    dc.PushOpacity(Math.Sin(t * Math.PI));
                    GoldStar(dc, new Point(65 + i * 18 + 6 * Math.Sin(t * 6 + i), 62 + t * 92), 2.3 + i % 2, t * 100);
                    dc.Pop();
                }
                break;
        }
        dc.Pop();
    }
}
