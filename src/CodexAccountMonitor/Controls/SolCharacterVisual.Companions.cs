using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexAccountMonitor.Core.Models;
using CodexAccountMonitor.Infrastructure;

namespace CodexAccountMonitor.Controls;

public sealed partial class SolCharacterVisual
{
    private sealed record ArmArt(Geometry Upper, Geometry Lower, Geometry Hand, Point Shoulder, Point Elbow, Point Wrist, double Fingers);
    private sealed record CompanionRig(Geometry Head, Geometry Body, Geometry LeftLeg, Geometry RightLeg,
        ArmArt Left, ArmArt Right, Point HeadPivot, double Scale, double CenterX, double SoleY);
    private sealed record CompanionArt(BitmapSource Bitmap, byte[] Pixels, CompanionRig Rig);
    private readonly Dictionary<DesktopModel, CompanionArt> _companionImages = new();
    private CompanionArt? _companionArt;

    private CompanionArt GetCompanionArt()
    {
        if (_companionImages.TryGetValue(Model, out var existing)) return existing;
        var source = new BitmapImage(new Uri($"pack://application:,,,/CodexAccountMonitor;component/Resources/Models/{DesktopModels.Name(Model)}.png"));
        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0); bitmap.Freeze();
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        var rig = Model switch
        {
            DesktopModel.Luna => CreateLunaRig(), DesktopModel.Terra => CreateTerraRig(), DesktopModel.Astra => CreateAstraRig(),
            _ => throw new InvalidOperationException("This Model uses another renderer.")
        };
        var art = new CompanionArt(bitmap, pixels, rig);
        _companionImages.Add(Model, art); return art;
    }

    private static CompanionRig CreateLunaRig() => new(
        Shape("M320,0 L940,0 L940,480 L850,498 L760,525 L647,526 L552,508 L452,474 L365,419 L325,360 Z"),
        Shape("M552,510 Q638,510 701,538 L730,579 L737,633 L725,688 L738,725 L730,760 L678,814 L623,819 L554,793 L513,758 L515,721 L536,682 L526,628 L520,579 Z"),
        Shape("M536,728 Q580,719 621,778 L623,855 L600,936 L610,1008 L604,1113 L615,1208 L570,1224 L416,1224 L415,1149 L423,1098 L446,1015 L478,930 L470,861 L475,787 Z"),
        Shape("M662,743 L715,726 L761,779 L774,857 L747,932 L775,979 L799,1082 L823,1160 L823,1220 L636,1220 L626,1156 L632,1071 L652,989 L671,937 L648,875 L640,801 Z"),
        new(Shape("M484,532 Q521,518 548,552 L539,587 L519,624 L499,670 L487,704 L446,685 L431,658 L446,598 L462,558 Z"),
            Shape("M444,659 Q473,654 492,680 L510,708 L494,750 L470,792 L440,819 L389,828 L362,795 L366,748 L397,697 Z"),
            Shape("M375,798 L431,807 L446,829 L461,874 L450,897 L428,886 L425,920 L409,937 L383,923 L359,890 L355,852 Z"),
            new(511,568), new(472,680), new(411,814), 96),
        new(Shape("M714,544 Q748,540 767,585 L781,662 L792,696 L749,724 L721,695 L708,643 L706,593 Z"),
            Shape("M757,675 Q786,673 810,700 L845,747 L868,791 L853,821 L805,838 L772,810 L747,760 L735,716 Z"),
            Shape("M813,809 L855,801 L877,827 L889,876 L873,918 L848,936 L825,927 L827,892 L805,891 L794,872 L798,839 Z"),
            new(737,581), new(762,694), new(833,817), 85),
        new(632,516), .162, 625, 1210);

    private static CompanionRig CreateTerraRig() => new(
        Shape("M285,0 L930,0 L930,440 L806,460 L695,479 L536,487 L450,474 L342,450 L288,422 Z"),
        Shape("M530,496 L562,484 L682,476 L721,506 L777,546 L811,621 L807,673 L785,718 L801,754 L803,803 L745,825 L687,850 L611,855 L558,843 L497,836 L452,810 L449,744 L474,708 L459,671 L459,600 L486,540 Z"),
        Shape("M479,811 L565,815 L625,848 L614,917 L587,958 L609,1018 L598,1088 L616,1173 L603,1238 L339,1238 L340,1190 L370,1111 L412,1067 L399,1007 L419,961 L461,913 Z"),
        Shape("M658,817 L750,796 L810,838 L825,910 L864,958 L885,1031 L874,1080 L934,1117 L980,1188 L981,1240 L651,1240 L655,1179 L673,1109 L662,1041 L653,988 L686,931 L667,872 Z"),
        new(Shape("M386,472 Q440,457 494,478 L514,512 L529,575 L512,627 L457,669 L425,709 L367,705 L313,670 L326,626 L338,566 L351,522 Z"),
            Shape("M319,655 L373,670 L408,697 L433,739 L418,793 L380,840 L341,856 L269,840 L236,798 L234,749 L260,701 Z"),
            Shape("M254,816 L340,826 L368,850 L387,879 L411,926 L394,952 L369,941 L351,980 L337,1003 L300,991 L269,959 L241,912 L234,867 Z"),
            new(448,568), new(380,683), new(307,839), 78),
        new(Shape("M752,476 Q814,457 864,481 L878,517 L906,560 L913,612 L933,656 L918,696 L866,718 L818,679 L789,641 L777,598 L750,539 Z"),
            Shape("M891,660 L932,664 L973,696 L1009,754 L1017,792 L1005,828 L948,856 L903,839 L877,802 L851,742 L854,697 Z"),
            Shape("M928,824 L996,813 L1017,857 L1016,904 L988,953 L949,992 L925,1005 L905,990 L901,966 L914,936 L892,931 L879,909 L888,876 Z"),
            new(824,568), new(891,683), new(964,839), 108),
        new(636,487), .159, 630, 1230);

    private void DrawCompanion(DrawingContext dc)
    {
        _companionArt = GetCompanionArt(); var rig = _companionArt.Rig;
        var f = CompanionPerformance.Sample(Model, _time);
        var stage = new Matrix(ActualWidth / 240, 0, 0, ActualHeight / 256, 0, 0);
        if (!stage.HasInverse) return;
        dc.PushTransform(new MatrixTransform(stage));
        var basis = Matrix.Identity; basis.Scale(rig.Scale, rig.Scale);
        basis.Translate(120 - rig.CenterX * rig.Scale, 244 - rig.SoleY * rig.Scale);
        var bodyPivot = basis.Transform(new Point(rig.CenterX, 650));
        var body = Matrix.Identity; body.RotateAt(f.BodyAngle, bodyPivot.X, bodyPivot.Y); body.Translate(f.BodyX, f.BodyY);
        var torso = basis; torso.Append(body);
        dc.DrawEllipse(Shadow, null, new Point(120, 246), Model == DesktopModel.Terra ? 50 : 35, 5);
        DrawCompanionLeg(dc, rig.LeftLeg, new(550, 790), f.LeftStep, true, basis, stage, f);
        DrawCompanionLeg(dc, rig.RightLeg, new(714, 790), f.RightStep, false, basis, stage, f);
        var left = CompanionArm(rig.Left, rig.Scale, basis, body, f.LeftUpper, f.LeftForearm);
        var right = CompanionArm(rig.Right, rig.Scale, basis, body, f.RightUpper, f.RightForearm);
        DrawCompanionShell(dc, rig.Left, left, false, stage, rig.Scale);
        DrawCompanionShell(dc, rig.Right, right, false, stage, rig.Scale);
        CompanionRaster(dc, rig.Body, torso, stage);
        var head = Matrix.Identity;
        var response = Math.Clamp((_time - _reactAt) / 1.25, 0, 1);
        var nod = response < 1 ? Math.Sin(response * Math.PI * 2) * 2 * (1 - response) : 0;
        head.RotateAt(f.HeadAngle + nod, rig.HeadPivot.X, rig.HeadPivot.Y); head.Append(basis); head.Append(body);
        CompanionRaster(dc, rig.Head, head, stage);
        DrawCompanionShell(dc, rig.Left, left, true, stage, rig.Scale);
        DrawCompanionShell(dc, rig.Right, right, true, stage, rig.Scale);
        DrawCompanionProps(dc, f, left, right);
        DrawCompanionHand(dc, rig.Left, left, f.LeftWrist, rig.Scale, stage);
        DrawCompanionHand(dc, rig.Right, right, f.RightWrist, rig.Scale, stage);
        dc.Pop();
    }

    private static SolLimb CompanionArm(ArmArt art, double scale, Matrix basis, Matrix body, double upperOffset, double lowerOffset)
    {
        var root = basis.Transform(art.Shoulder);
        var upper = art.Elbow - art.Shoulder; var lower = art.Wrist - art.Elbow;
        var a = Math.Atan2(upper.Y, upper.X) + upperOffset * Math.PI / 180;
        var b = Math.Atan2(lower.Y, lower.X) + lowerOffset * Math.PI / 180;
        var elbow = new Point(root.X + upper.Length * scale * Math.Cos(a), root.Y + upper.Length * scale * Math.Sin(a));
        var wrist = new Point(elbow.X + lower.Length * scale * Math.Cos(b), elbow.Y + lower.Length * scale * Math.Sin(b));
        return new(ToSol(body.Transform(root)), ToSol(body.Transform(elbow)), ToSol(body.Transform(wrist)));
    }

    private void DrawCompanionShell(DrawingContext dc, ArmArt art, SolLimb arm, bool lower, Matrix stage, double scale) =>
        CompanionRaster(dc, lower ? art.Lower : art.Upper,
            SegmentTransform(lower ? art.Elbow : art.Shoulder, lower ? art.Wrist : art.Elbow,
                P(lower ? arm.Joint : arm.Root), P(lower ? arm.End : arm.Joint), false, scale), stage);

    private void DrawCompanionHand(DrawingContext dc, ArmArt art, SolLimb arm, double flex, double scale, Matrix stage)
    {
        var source = art.Wrist - art.Elbow;
        var sourceAngle = Math.Atan2(source.Y, source.X) * 180 / Math.PI;
        var current = P(arm.End) - P(arm.Joint);
        var forearm = Math.Atan2(current.Y, current.X) * 180 / Math.PI;
        var fingerAngle = forearm + Math.Clamp(art.Fingers - sourceAngle + flex, -30, 30);
        var transform = Matrix.Identity; transform.Translate(-art.Wrist.X, -art.Wrist.Y);
        transform.Scale(scale, scale); transform.Rotate(fingerAngle - art.Fingers); transform.Translate(arm.End.X, arm.End.Y);
        CompanionRaster(dc, art.Hand, transform, stage);
    }

    private void DrawCompanionLeg(DrawingContext dc, Geometry geometry, Point hip, double step, bool left, Matrix basis, Matrix stage, CompanionFrame f)
    {
        var transform = Matrix.Identity; transform.RotateAt(step * (left ? -1.5 : 1.5), hip.X, hip.Y);
        transform.Append(basis); transform.Translate(f.BodyX * .2, -step);
        CompanionRaster(dc, geometry, transform, stage);
    }

    private void CompanionRaster(DrawingContext dc, Geometry geometry, Matrix transform, Matrix stage)
    {
        var art = _companionArt!;
        dc.PushTransform(new MatrixTransform(transform)); dc.PushClip(geometry);
        dc.DrawImage(art.Bitmap, new Rect(0, 0, art.Bitmap.PixelWidth, art.Bitmap.PixelHeight)); dc.Pop(); dc.Pop();
        transform.Append(stage); _hitLayers.Add((geometry, transform, true));
    }

}
