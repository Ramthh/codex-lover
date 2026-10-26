using System.Windows;
using System.Windows.Media;
using CodexAccountMonitor.Infrastructure;

namespace CodexAccountMonitor.Controls;

public sealed partial class SolCharacterVisual
{
    private static readonly Geometry HeartShape = Shape("M0,17 C-5,12 -25,-1 -25,-13 C-25,-29 -8,-30 0,-17 C8,-30 25,-29 25,-13 C25,-1 5,12 0,17 Z");
    private static readonly Brush HeartMaterial = Gradient("#CB84D4", "#FFD0EC", "#A679D2");
    private static readonly Brush PropShadow = Solid("#4C081529");
    private static readonly Brush ScreenMaterial = Gradient("#263D53", "#162C42", "#102337", true);

    private void DrawProps(DrawingContext dc, SolPerformanceFrame f)
    {
        if (f.PropScale <= .0001) return;
        dc.PushTransform(new TranslateTransform(f.Prop.X, f.Prop.Y));
        dc.PushTransform(new RotateTransform(f.PropAngle));
        dc.PushTransform(new ScaleTransform(f.PropScale, f.PropScale));
        switch (f.Act)
        {
            case SolAct.Build: DrawLaptop(dc, f); break;
            case SolAct.Heart:
                dc.PushTransform(new TranslateTransform(1, 2)); dc.DrawGeometry(PropShadow, null, HeartShape); dc.Pop();
                dc.DrawGeometry(HeartMaterial, new Pen(Solid("#FFF0FA"), .75), HeartShape);
                dc.DrawGeometry(Solid("#A6FFFFFF"), null, Shape("M-18,-14 C-20,-21 -12,-25 -8,-21 C-6,-18 -9,-16 -13,-17 Q-16,-18 -18,-14 Z"));
                dc.DrawGeometry(null, RoundPen(Solid("#85FFE5F9"), .8), Shape("M-21,-3 Q-15,7 0,15"));
                break;
            case SolAct.Celebrate: DrawGift(dc, f); break;
            case SolAct.Inspect: DrawMagnifier(dc); break;
        }
        dc.Pop(); dc.Pop(); dc.Pop();
    }

    private static void DrawLaptop(DrawingContext dc, SolPerformanceFrame f)
    {
        dc.DrawRoundedRectangle(PropShadow, null, new Rect(-38, -38, 77, 45), 4, 4);
        dc.DrawRoundedRectangle(Gradient("#8C86AA", "#F4EDFF", "#AAA0C5"), new Pen(Solid("#D8CAE7"), .6), new Rect(-37, -40, 74, 44), 4, 4);
        dc.DrawRoundedRectangle(ScreenMaterial, new Pen(Solid("#746D94"), .6), new Rect(-33, -36, 66, 35), 2.4, 2.4);
        dc.DrawEllipse(Solid("#99CED7DE"), null, new Point(0, -38), .6, .6);
        dc.DrawLine(new Pen(Solid("#476C819C"), .5), new Point(-29, -28), new Point(29, -28));
        foreach (var (x, color) in new[] { (-28.0, "#DCA8BA"), (-24.0, "#D9CE9C"), (-20.0, "#8DC9B7") })
            dc.DrawEllipse(Solid(color), null, new Point(x, -32), 1, 1);
        var typed = (int)(f.Phase * 6) % 21;
        for (var row = 0; row < 4; row++)
        {
            var length = Math.Clamp(typed - row * 4, 0, 5) * 6;
            if (length <= 0) continue;
            var start = -25 + row % 2 * 6;
            dc.DrawLine(RoundPen(row == 2 ? Pink : row % 2 == 0 ? Cyan : Lavender, 1.25), new Point(start, -23 + row * 5.8), new Point(start + length, -23 + row * 5.8));
        }
        if (((int)(f.Phase * 2) & 1) == 0) dc.DrawRectangle(Cyan, null, new Rect(24, -6, 1.3, 2));
        dc.DrawGeometry(Gradient("#C9BEDF", "#FCF8FF", "#AFA4C6"), new Pen(Solid("#968AAC"), .55), Shape("M-37,4 L37,4 L45,12 Q43,15 0,16 Q-43,15 -45,12 Z"));
        dc.DrawGeometry(Solid("#B6ACC9"), null, Shape("M-28,5 L28,5 L33,10 L-33,10 Z"));
        for (var x = -24; x <= 24; x += 6) dc.DrawLine(new Pen(Solid("#DDD4E9"), .6), new Point(x, 5.5), new Point(x, 9.5));
        dc.DrawLine(new Pen(Solid("#DDD4E9"), .6), new Point(-29, 7.5), new Point(29, 7.5));
        dc.DrawRoundedRectangle(Solid("#D0C4DF"), new Pen(Solid("#B4A7C9"), .3), new Rect(-8, 11, 16, 3), 1, 1);
        dc.DrawLine(new Pen(Solid("#E7FDF7"), .75), new Point(-38, 13), new Point(38, 13));
    }

    private static void DrawGift(DrawingContext dc, SolPerformanceFrame f)
    {
        dc.DrawEllipse(PropShadow, null, new Point(0, 18), 22, 3.5);
        dc.DrawGeometry(Gradient("#AAA0D4", "#E2D9F8", "#B8A2D9"), new Pen(Solid("#EBDEFB"), .6), Shape("M-18,-15 L18,-15 L18,13 Q0,19 -18,13 Z"));
        dc.DrawRectangle(Gradient("#D4A4D7", "#FFE0F3", "#C78DCB"), null, new Rect(-3.5, -15, 7, 31));
        var opening = EaseProp((f.Phase - 2.35) / .8) * (1 - EaseProp((f.Phase - 8.8) / .8));
        var lidLift = 17 * opening;
        dc.PushTransform(new TranslateTransform(opening * 3, -lidLift));
        dc.PushTransform(new RotateTransform(-opening * 9, -18, -20));
        dc.DrawRoundedRectangle(Gradient("#C48AC6", "#FCD8EF", "#B781C5"), new Pen(Solid("#FFEAF8"), .7), new Rect(-21, -22, 42, 8), 2, 2);
        dc.DrawGeometry(null, RoundPen(Cyan, 2.6), Shape("M0,-22 C-18,-34 -18,-18 0,-22 C18,-34 18,-18 0,-22"));
        dc.Pop(); dc.Pop();
        if (opening > .001)
        {
            dc.PushOpacity(opening * .6);
            dc.DrawEllipse(Radial("#939AFBE9", "#009AFBE9"), null, new Point(0, -17), 28, 20);
            dc.Pop();
        }
    }

    private static void DrawMagnifier(DrawingContext dc)
    {
        dc.DrawLine(RoundPen(Solid("#8F81AE"), 6), new Point(-5, 13), new Point(-8, 23));
        dc.DrawLine(RoundPen(Gradient("#A395C5", "#EBE0FF", "#A798C8"), 4.2), new Point(-5, 13), new Point(-8, 23));
        dc.DrawEllipse(Gradient("#9486BD", "#EEE5FF", "#A895CD"), new Pen(Solid("#F4ECFF"), .6), new Point(0, 0), 16.7, 16.7);
        dc.DrawEllipse(Gradient("#884783A1", "#745DADC1", "#99152B51", true), new Pen(Cyan, .75), new Point(0, 0), 13.8, 13.8);
        dc.DrawGeometry(null, RoundPen(Solid("#D5ECFEFF"), 1.5), Shape("M-9,3 C-11,-5 -6,-11 2,-10"));
        Star(dc, new Point(4, 3), 4.5, 12, Solid("#C5CAFAF4"));
    }

    private static void DrawLanding(DrawingContext dc, SolPerformanceFrame f)
    {
        if (f.Landing < .01) return;
        dc.PushOpacity(f.Landing * .35);
        dc.DrawEllipse(null, new Pen(Solid("#98CFE4DD"), .9), new Point(122, 244), 44 + (1 - f.Landing) * 11, 5.5);
        dc.Pop();
    }

    private static void DrawAccentParticles(DrawingContext dc, SolPerformanceFrame f)
    {
        if (f.Energy < .01 || f.Act is SolAct.Greet or SolAct.Build or SolAct.Inspect) return;
        if (f.Act == SolAct.Heart)
        {
            // A small, finite heart trail after the offer; no constant particle cloud.
            for (var i = 0; i < 3; i++)
            {
                var age = (f.Phase - 4 - i * .44) / 2.3;
                if (age is <= 0 or >= 1) continue;
                var x = f.Prop.X + (i - 1) * (14 + age * 8);
                var y = f.Prop.Y - 30 - age * 28;
                dc.PushOpacity(Math.Sin(age * Math.PI) * f.Energy * .65);
                dc.PushTransform(new TranslateTransform(x, y)); dc.PushTransform(new ScaleTransform(.10, .10));
                dc.DrawGeometry(HeartMaterial, null, HeartShape); dc.Pop(); dc.Pop(); dc.Pop();
            }
        }
        else
        {
            // Two coordinated bursts from the box. Gravity brings the confetti down.
            foreach (var launch in new[] { 2.95, 6.15 })
            {
                var age = f.Phase - launch;
                if (age is <= 0 or >= 2.1) continue;
                for (var i = 0; i < 9; i++)
                {
                    var vx = (i - 4) * 4;
                    var vy = -34 - (i % 3) * 7;
                    var x = f.Prop.X + vx * age;
                    var y = f.Prop.Y - 21 + vy * age + 15 * age * age;
                    dc.PushOpacity(Math.Clamp((2.1 - age) * .7, 0, .9) * f.Energy);
                    if (i % 3 == 0) Star(dc, new Point(x, y), 2.4, age * 120 + i * 10, Cyan);
                    else
                    {
                        dc.PushTransform(new RotateTransform(age * 150 + i * 28, x, y));
                        dc.DrawRoundedRectangle(i % 2 == 0 ? Pink : Lavender, null, new Rect(x - 1, y - 2, 2, 4), .7, .7); dc.Pop();
                    }
                    dc.Pop();
                }
            }
        }
    }

    private static void Star(DrawingContext dc, Point center, double size, double angle, Brush fill)
    {
        dc.PushTransform(new TranslateTransform(center.X, center.Y)); dc.PushTransform(new RotateTransform(angle));
        var shape = new StreamGeometry();
        using (var g = shape.Open())
        {
            g.BeginFigure(new Point(0, -size), true, true);
            for (var i = 1; i < 8; i++)
            {
                var a = i * Math.PI / 4 - Math.PI / 2; var radius = i % 2 == 0 ? size : size * .32;
                g.LineTo(new Point(Math.Cos(a) * radius, Math.Sin(a) * radius), true, false);
            }
        }
        dc.DrawGeometry(fill, null, shape); dc.Pop(); dc.Pop();
    }
    private static double EaseProp(double value) { var t = Math.Clamp(value, 0, 1); return t * t * t * (t * (t * 6 - 15) + 10); }
}
