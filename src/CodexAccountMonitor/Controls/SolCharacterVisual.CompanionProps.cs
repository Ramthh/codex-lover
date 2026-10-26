using System.Windows;
using System.Windows.Media;
using CodexAccountMonitor.Infrastructure;

namespace CodexAccountMonitor.Controls;

public sealed partial class SolCharacterVisual
{
    private static Point HandsCenter(SolLimb left, SolLimb right) => new((left.End.X + right.End.X) / 2, (left.End.Y + right.End.Y) / 2);
    private static void At(DrawingContext dc, Point point, double scale, double angle, Action draw)
    {
        dc.PushTransform(new TranslateTransform(point.X, point.Y));
        dc.PushTransform(new RotateTransform(angle)); dc.PushTransform(new ScaleTransform(scale, scale));
        draw(); dc.Pop(); dc.Pop(); dc.Pop();
    }

    private void DrawCompanionProps(DrawingContext dc, CompanionFrame f, SolLimb left, SolLimb right)
    {
        if (f.PropScale < .001) return;
        if (Model == Core.Models.DesktopModel.Astra) { DrawAstraProps(dc, f, left, right); return; }
        var center = HandsCenter(left, right);
        switch (f.Act)
        {
            case CompanionAct.Read:
                At(dc, new Point(center.X, center.Y + 1), f.PropScale, -2, () =>
                {
                    dc.DrawGeometry(Gradient("#9482BE", "#D9C8FA", "#8873B4"), new Pen(Solid("#DAD1EF"), .6),
                        Shape("M-29,-18 Q-15,-23 0,-17 Q15,-23 29,-18 L29,15 Q15,11 0,17 Q-15,11 -29,15 Z"));
                    dc.DrawGeometry(Solid("#F7F3FF"), null, Shape("M-26,-17 Q-12,-20 -1,-15 L-1,13 Q-13,9 -26,12 Z M1,-15 Q12,-20 26,-17 L26,12 Q13,9 1,13 Z"));
                    dc.DrawLine(new Pen(Solid("#AF9CCF"), 1), new Point(0,-15), new Point(0,15));
                    for (var row = 0; row < 4; row++)
                    {
                        dc.DrawLine(new Pen(Solid("#C0B3D5"), .8), new Point(-22,-10+row*5), new Point(-5,-8+row*5));
                        dc.DrawLine(new Pen(Solid("#C0B3D5"), .8), new Point(5,-8+row*5), new Point(22,-10+row*5));
                    }
                    var page = Math.Sin(f.Phase * .8);
                    if (page > .65)
                    {
                        dc.PushOpacity((page - .65) / .35);
                        dc.DrawGeometry(Solid("#FDFBFF"), new Pen(Solid("#BDB0D6"), .35), Shape("M0,-16 Q14,-22 20,-18 Q12,-7 0,14 Z")); dc.Pop();
                    }
                    dc.DrawGeometry(Pink, null, Shape("M15,10 L18,10 L18,19 L16.5,17 L15,19 Z"));
                });
                break;
            case CompanionAct.Moonlight:
                At(dc, new Point(right.End.X + 3, right.End.Y - 14), f.PropScale, -8, () =>
                {
                    dc.DrawEllipse(Radial("#758FBAF0", "#008FBAF0"), null, new Point(0,0), 24, 24);
                    dc.DrawGeometry(Gradient("#A993DE", "#F9EDFF", "#C5ABF0"), new Pen(Solid("#EFE4FF"), .6),
                        Shape("M5,-15 A16,16 0 1 0 14,10 A14,14 0 0 1 5,-15 Z"));
                });
                for (var i = 0; i < 5; i++)
                {
                    var a = f.Phase * .6 + i * 1.25;
                    dc.PushOpacity(f.Energy * (.45 + .2 * Math.Sin(a * 2)));
                    Star(dc, new Point(right.End.X + Math.Cos(a) * 22, right.End.Y - 17 + Math.Sin(a) * 17), 1.8, a * 20, Lavender); dc.Pop();
                }
                break;
            case CompanionAct.Garden:
                At(dc, new Point(194, 226), f.PropScale, 0, () =>
                {
                    dc.DrawGeometry(Gradient("#A98CBC", "#E0CAE7", "#937BAB"), null, Shape("M-10,0 L10,0 L7,16 L-7,16 Z"));
                    dc.DrawRoundedRectangle(Lavender, null, new Rect(-12,-3,24,5), 2, 2);
                    dc.DrawLine(RoundPen(Solid("#84BAA4"), 2), new Point(0,0), new Point(-1,-26));
                    dc.DrawGeometry(Solid("#8BC5AF"), null, Shape("M0,-9 C-17,-9 -16,-22 0,-16 M0,-14 C15,-20 17,-9 0,-8"));
                    var bloom = CompanionPerformance.Ease((f.Phase - 4) / 2);
                    At(dc, new Point(-1,-28), .6 + .4 * bloom, Math.Sin(f.Phase) * 5, () =>
                    {
                        for (var petal = 0; petal < 5; petal++)
                        {
                            dc.PushTransform(new RotateTransform(petal * 72));
                            dc.DrawEllipse(Pink, null, new Point(0,-4), 3.5, 5); dc.Pop();
                        }
                        dc.DrawEllipse(Solid("#F8EBC2"), null, new Point(), 2.5, 2.5);
                    });
                });
                var can = new Point(right.End.X + 5, right.End.Y + 5);
                At(dc, can, f.PropScale, 14 + Math.Sin(f.Phase) * 4, () =>
                {
                    dc.DrawEllipse(null, new Pen(Lavender, 2.5), new Point(-7,-4), 7, 8);
                    dc.DrawRoundedRectangle(Gradient("#9D90CA", "#E3DDF6", "#A496D1"), new Pen(Solid("#DFD5F4"), .6), new Rect(-9,-6,21,17), 5, 5);
                    dc.DrawGeometry(Lavender, null, Shape("M10,3 L24,-2 L26,1 L12,9 Z"));
                });
                for (var i = 0; i < 4; i++)
                {
                    var t = ((f.Phase * 1.3 + i * .23) % 1);
                    dc.PushOpacity(f.Energy * Math.Sin(t * Math.PI));
                    dc.DrawEllipse(Cyan, null, new Point(can.X + 23 + (193 - can.X - 23) * t, can.Y + (211 - can.Y) * t), 1, 2); dc.Pop();
                }
                break;
            case CompanionAct.Music:
                for (var i = 0; i < 4; i++)
                {
                    var t = (f.Phase * .24 + i * .25) % 1;
                    At(dc, new Point(73 + i * 30 + Math.Sin(t * 5) * 4, 145 - t * 45), f.PropScale * Math.Sin(t * Math.PI), -10, () =>
                    {
                        dc.DrawEllipse(i % 2 == 0 ? Lavender : Pink, null, new Point(), 3.5, 2.5);
                        dc.DrawLine(RoundPen(Lavender, 1.5), new Point(3,0), new Point(3,-13));
                        dc.DrawGeometry(null, RoundPen(Lavender, 1.5), Shape("M3,-13 Q12,-9 7,-6"));
                    });
                }
                break;
            case CompanionAct.Breathe:
                At(dc, new Point(center.X, center.Y - 5), f.PropScale, 0, () =>
                {
                    var radius = 5 + 1.8 * Math.Sin((f.Phase - 2.5) * 1.15);
                    dc.DrawEllipse(Radial("#B1D4C9FF", "#00D4C9FF"), null, new Point(), radius * 2.4, radius * 2.4);
                    dc.DrawEllipse(Solid("#B8E6DEFF"), new Pen(Lavender, .6), new Point(), radius, radius);
                }); break;
            case CompanionAct.Diagnose:
                At(dc, new Point(center.X, center.Y - 4), f.PropScale, -2, () =>
                {
                    dc.DrawRoundedRectangle(Gradient("#294559", "#173040", "#102635"), new Pen(Cyan, .7), new Rect(-25,-18,50,30), 4,4);
                    dc.DrawGeometry(null, RoundPen(Cyan, 1.1), Shape("M-20,0 L-13,0 L-10,-7 L-6,5 L-2,-11 L3,0 L19,0"));
                    dc.DrawLine(new Pen(Solid("#8A83F8F0"), .7), new Point(-21,-13), new Point(-21 + (f.Phase % 3) * 14,-13));
                    if (f.Phase > 6) dc.DrawGeometry(null, RoundPen(Solid("#A6F1C3"), 1.4), Shape("M12,6 L15,9 L21,3"));
                }); break;
            case CompanionAct.Repair:
                var tool = new Point(right.End.X + 7, right.End.Y + 4);
                At(dc, new Point(tool.X + 3, tool.Y + 24), f.PropScale, 0, () =>
                {
                    dc.DrawRoundedRectangle(Gradient("#455465", "#6F8292", "#303D4D"), new Pen(Solid("#AEC3D4"), .6), new Rect(-14,-5,28,17), 3,3);
                    dc.DrawEllipse(Solid("#97AABD"), new Pen(Ink,.6), new Point(), 5, 5);
                    dc.DrawLine(new Pen(Ink,1.2), new Point(-3,0),new Point(3,0));
                });
                At(dc, tool, f.PropScale, -12 + 16 * Math.Sin(f.Phase * 3.2), () =>
                {
                    dc.DrawLine(RoundPen(Solid("#A3B7C8"), 4.5), new Point(0,-5), new Point(0,19));
                    dc.DrawGeometry(Solid("#C9D5DF"), new Pen(Solid("#627A8E"), .5), Shape("M-4,15 L-7,20 L-4,26 L4,26 L7,20 L4,15 L3,22 L-3,22 Z"));
                    dc.DrawEllipse(Solid("#536A7D"), null, new Point(0,-4), 1.6,1.6);
                }); break;
            case CompanionAct.Carry:
                At(dc, new Point(center.X, center.Y - 3), f.PropScale, f.BodyAngle, () =>
                {
                    dc.DrawRoundedRectangle(Gradient("#746375", "#BCA9AD", "#786773"), new Pen(Solid("#D3C3CF"), .7), new Rect(-24,-19,48,32), 3,3);
                    dc.DrawRectangle(Solid("#DDC7B5"), null, new Rect(-3,-19,6,32));
                    dc.DrawLine(new Pen(Solid("#685B68"),1),new Point(-23,-10),new Point(23,-10));
                    dc.DrawGeometry(null, RoundPen(Solid("#FAEADC"),1.2),Shape("M-16,5 L-16,-2 M-19,1 L-16,-2 L-13,1"));
                }); break;
            case CompanionAct.Reassure:
                At(dc, new Point(right.End.X + 3, right.End.Y - 12), f.PropScale * (.30 + .02 * Math.Sin(f.Phase * 2.5)), 0, () =>
                {
                    dc.DrawGeometry(Gradient("#49BFC7", "#BDFFF1", "#6493BE"), new Pen(Cyan, 1.5), HeartShape);
                }); break;
        }
    }
}
