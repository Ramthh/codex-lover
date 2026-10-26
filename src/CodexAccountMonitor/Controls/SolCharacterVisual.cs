using System.Diagnostics;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexAccountMonitor.Infrastructure;
using CodexAccountMonitor.Core.Models;

namespace CodexAccountMonitor.Controls;

/// <summary>Sol's original head and torso, articulated limbs, and coordinated performance.</summary>
public sealed partial class SolCharacterVisual : FrameworkElement, IDisposable
{
    private const double ArtScale = .158;
    private readonly BitmapSource _art;
    private readonly byte[] _pixels;
    private readonly Stopwatch _clock = new();
    private readonly List<(Geometry Shape, Matrix Transform, bool Raster)> _hitLayers = [];
    private double _time, _startTime, _nextTick;
    private double _reactAt = -10;
    private bool _running, _disposed;
    public DesktopModel Model { get; private set; } = DesktopModel.Sol;

    public void SetModel(DesktopModel model)
    {
        if (!Enum.IsDefined(model)) throw new ArgumentOutOfRangeException(nameof(model));
        if (Model == model) return;
        StopMotion(); Model = model; _time = 0; _reactAt = -10;
        _hitLayers.Clear(); InvalidateVisual();
    }
    private static readonly Point HipCenter = new(120, 177);
    private static readonly ConcurrentDictionary<string, Geometry> Shapes = new();
    private static readonly ConcurrentDictionary<string, SolidColorBrush> Solids = new();
    private static readonly ConcurrentDictionary<(string, string, string, bool), Brush> Gradients = new();
    private static readonly ConcurrentDictionary<(string, string), Brush> Radials = new();
    private static readonly Geometry Head = Shape("M180,0 L900,0 L900,400 L875,463 L847,496 L823,547 L793,578 L736,609 L676,628 L616,647 L546,664 L465,674 L360,672 L301,644 L249,622 L203,603 Z");
    private static readonly Geometry Torso = Shape("M534,644 L674,610 L739,635 L778,714 L802,815 L793,871 L765,908 L724,929 L674,942 L577,938 L529,915 L502,866 L489,796 L499,718 Z");
    // These masks retain the real Sol shell, mittens and distinct boots. All
    // articulation happens at the photographed joint centers, never at a toe.
    private static readonly Geometry UpperArm = Shape("M475,681 C494,677 507,697 502,725 L484,782 Q474,800 449,799 Q431,796 414,783 Q409,778 410,771 Q416,736 437,704 Q454,686 475,681 Z");
    private static readonly Geometry Forearm = Shape("M391,773 C407,767 425,780 442,796 C461,813 471,826 463,850 C459,876 438,902 411,925 L372,917 L312,895 C301,883 303,860 312,836 C328,808 362,780 391,773 Z");
    private static readonly Geometry ClosedHand = Shape("M306,895 L371,916 L400,912 L417,945 L411,977 L382,989 L360,975 L353,999 L321,1005 L293,985 L282,950 Z");
    private static readonly Geometry OpenHand = Shape("M899,548 L874,482 L876,448 L907,427 L932,441 L940,473 L945,434 L961,409 L988,405 L1007,429 L998,478 L1024,445 L1050,444 L1070,467 L1057,501 L1080,501 L1094,524 L1081,550 L1012,596 L971,615 L926,595 Z");
    private static readonly Geometry LeftShin = Shape("M535,896 L636,923 L628,985 L596,1057 L486,1048 L477,996 L500,934 Z");
    private static readonly Geometry RightShin = Shape("M683,910 L781,885 L795,939 L800,1009 L781,1057 L678,1055 L670,989 Z");
    private static readonly Geometry LeftBoot = Shape("M495,1025 L598,1043 L615,1092 L624,1170 L608,1219 L567,1230 L355,1230 L355,1116 L403,1076 L472,1034 Z");
    private static readonly Geometry RightBoot = Shape("M688,1024 L780,1025 L814,1069 L866,1112 L880,1228 L642,1228 L638,1120 L664,1057 Z");
    private static readonly Geometry Visor = Shape("M354,351 C427,293 555,260 651,242 C709,229 748,245 772,289 C795,336 809,403 806,456 C805,502 770,530 718,548 C627,581 507,614 428,617 C379,619 348,594 331,547 C308,489 306,424 327,384 C334,370 343,359 354,351 Z");
    private static readonly Brush White = Solid("#F8F5FF"), Cyan = Solid("#83F8F0"), Lavender = Solid("#BCA8EE"), Pink = Solid("#F4B4E9"), Ink = Solid("#13223C");
    private static readonly Brush Shell = Gradient("#8D87B1", "#FCFAFF", "#BBB0DC");
    private static readonly Brush ShellFront = Gradient("#C5BADD", "#FFFFFF", "#A899CF");
    private static readonly Brush JointBrush = Gradient("#68658E", "#C1B2E9", "#79729E");
    private static readonly Brush FaceGlass = Gradient("#263C6C", "#14264A", "#09162E", vertical: true);
    private static readonly Brush Shadow = Radial("#80516C83", "#00516C83");
    private static readonly Brush ChestGlow = Radial("#BC9EFFF8", "#009EFFF8");

    public SolCharacterVisual()
    {
        Width = 240; Height = 256;
        var source = new BitmapImage(new Uri("pack://application:,,,/CodexAccountMonitor;component/Resources/Sol/robot-wave.png"));
        _art = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        _art.Freeze();
        _pixels = new byte[_art.PixelWidth * _art.PixelHeight * 4];
        _art.CopyPixels(_pixels, _art.PixelWidth * 4, 0);
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        Unloaded += (_, _) => StopMotion();
    }

    public void StartMotion()
    {
        if (_disposed || _running) return;
        _running = true; _startTime = _time; _nextTick = 0; _clock.Restart();
        CompositionTarget.Rendering += OnFrame;
    }
    public void StopMotion()
    {
        if (!_running) return;
        _running = false; _clock.Stop(); CompositionTarget.Rendering -= OnFrame;
    }
    public void Seek(double seconds)
    {
        StopMotion(); _time = double.IsFinite(seconds) ? seconds : 0; _reactAt = -10; InvalidateVisual();
    }
    public void React()
    {
        if (!_running || _time - _reactAt < 1.6) return;
        _reactAt = _time; InvalidateVisual();
    }
    private void OnFrame(object? sender, EventArgs args)
    {
        var elapsed = _clock.Elapsed.TotalSeconds;
        if (!_running || elapsed + .001 < _nextTick) return;
        _nextTick += 1.0 / 60;
        if (_nextTick < elapsed - 1.0 / 60) _nextTick = elapsed + 1.0 / 60;
        _time = _startTime + elapsed; InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); _hitLayers.Clear();
        if (Model != DesktopModel.Sol) { DrawCompanion(dc); return; }
        var f = SolPerformance.Sample(_time);
        var stage = new Matrix(ActualWidth / 240, 0, 0, ActualHeight / 256, 0, 0);
        if (!stage.HasInverse) return;
        dc.PushTransform(new MatrixTransform(stage));
        var response = Math.Clamp((_time - _reactAt) / 1.25, 0, 1);
        var nod = response < 1 ? Math.Sin(response * Math.PI * 2) * 3 * (1 - response) : 0;
        var bodyMatrix = Matrix.Identity;
        bodyMatrix.RotateAt(f.BodyAngle, HipCenter.X, HipCenter.Y); bodyMatrix.Translate(f.BodyX, f.BodyY);
        Point Anchor(double x, double y) => bodyMatrix.Transform(new Point(x, y));
        var leftHip = Anchor(110, 199); var rightHip = Anchor(133, 199);
        var leftPose = SolArmPerformance.Sample(f, true);
        var rightPose = SolArmPerformance.Sample(f, false);
        var leftArm = leftPose.Limb; var rightArm = rightPose.Limb;
        var footLift = Math.Max(0, 220 - Math.Min(f.LeftFoot.Y, f.RightFoot.Y));
        dc.PushOpacity(Math.Clamp(1 - footLift * .022, .35, 1));
        dc.DrawEllipse(Shadow, null, new Point(122 + f.BodyX * .3, 244), 47 - footLift * .35, 7); dc.Pop();
        DrawLanding(dc, f);
        DrawLeg(dc, leftHip, P(f.LeftFoot), true, stage); DrawLeg(dc, rightHip, P(f.RightFoot), false, stage);
        DrawArmShell(dc, leftArm, true, false, stage); DrawArmShell(dc, rightArm, false, false, stage);
        var torsoMatrix = Matrix.Identity;
        torsoMatrix.Scale(ArtScale, ArtScale); torsoMatrix.Translate(120 - 640 * ArtScale, 177 - 792 * ArtScale); torsoMatrix.Append(bodyMatrix);
        Raster(dc, Torso, torsoMatrix, stage);
        dc.PushTransform(new MatrixTransform(torsoMatrix)); dc.PushOpacity(.19 + .08 * Math.Sin(_time * 2.1));
        dc.DrawEllipse(ChestGlow, null, new Point(645, 767), 54, 54); dc.Pop(); dc.Pop();
        var headMatrix = Matrix.Identity;
        headMatrix.Translate(-615, -636); headMatrix.Rotate(f.HeadAngle + nod); headMatrix.Scale(ArtScale, ArtScale);
        headMatrix.Translate(116 + f.HeadX, 152 + f.HeadY); headMatrix.Append(bodyMatrix);
        Raster(dc, Head, headMatrix, stage); DrawFace(dc, f, headMatrix);
        DrawArmShell(dc, leftArm, true, true, stage); DrawArmShell(dc, rightArm, false, true, stage);
        DrawProps(dc, f);
        DrawHand(dc, leftPose, true, stage);
        DrawHand(dc, rightPose, false, stage);
        DrawAccentParticles(dc, f); dc.Pop();
    }

    private void Raster(DrawingContext dc, Geometry geometry, Matrix transform, Matrix stage)
    {
        dc.PushTransform(new MatrixTransform(transform)); dc.PushClip(geometry);
        dc.DrawImage(_art, new Rect(0, 0, _art.PixelWidth, _art.PixelHeight)); dc.Pop(); dc.Pop();
        transform.Append(stage); _hitLayers.Add((geometry, transform, true));
    }
    private void Track(Geometry geometry, Matrix transform, Matrix stage)
    {
        transform.Append(stage); _hitLayers.Add((geometry, transform, false));
    }
    public bool HitTestCharacter(Point point)
    {
        var bitmap = Model == DesktopModel.Sol ? _art : _companionArt?.Bitmap;
        var pixels = Model == DesktopModel.Sol ? _pixels : _companionArt?.Pixels;
        if (bitmap is null || pixels is null) return false;
        foreach (var (shape, transform, raster) in _hitLayers)
        {
            var inverse = transform;
            if (!inverse.HasInverse) continue;
            inverse.Invert(); var p = inverse.Transform(point);
            if (!shape.FillContains(p)) continue;
            if (!raster) return true;
            var x = (int)Math.Floor(p.X); var y = (int)Math.Floor(p.Y);
            if (x >= 0 && y >= 0 && x < bitmap.PixelWidth && y < bitmap.PixelHeight && pixels[(y * bitmap.PixelWidth + x) * 4 + 3] > 30) return true;
        }
        return false;
    }
    private void DrawLeg(DrawingContext dc, Point hip, Point ankle, bool left, Matrix stage)
    {
        // Sol has compact shell legs. A single shin follows the ankle without
        // inventing a deep sideways human knee in the original short silhouette.
        var sourceHip = left ? new Point(570, 925) : new Point(729, 916);
        var sourceAnkle = left ? new Point(539, 1053) : new Point(741, 1053);
        Raster(dc, left ? LeftShin : RightShin,
            SegmentTransform(sourceHip, sourceAnkle, hip, ankle, false, .158), stage);
        var boot = Matrix.Identity;
        boot.Translate(-sourceAnkle.X, -sourceAnkle.Y);
        boot.Scale(.145, .145); boot.Translate(ankle.X, ankle.Y);
        Raster(dc, left ? LeftBoot : RightBoot, boot, stage);
    }

    private void DrawArmShell(DrawingContext dc, SolLimb arm, bool left, bool forearm, Matrix stage)
    {
        var sourceRoot = forearm ? new Point(424, 790) : new Point(485, 706);
        var sourceEnd = forearm ? new Point(346, 903) : new Point(424, 790);
        var root = forearm ? P(arm.Joint) : P(arm.Root);
        var end = forearm ? P(arm.End) : P(arm.Joint);
        Raster(dc, forearm ? Forearm : UpperArm,
            SegmentTransform(sourceRoot, sourceEnd, root, end, !left, .143), stage);
    }

    private static Matrix SegmentTransform(Point sourceRoot, Point sourceEnd,
        Point root, Point end, bool mirror, double shellWidthScale)
    {
        var source = sourceEnd - sourceRoot;
        var destination = end - root;
        var sourceAngle = Math.Atan2(source.Y, mirror ? -source.X : source.X) * 180 / Math.PI;
        var transform = Matrix.Identity;
        transform.Translate(-sourceRoot.X, -sourceRoot.Y);
        if (mirror) transform.Scale(-1, 1);
        transform.Rotate(-sourceAngle);
        transform.Scale(destination.Length / source.Length, shellWidthScale);
        transform.Rotate(Math.Atan2(destination.Y, destination.X) * 180 / Math.PI);
        transform.Translate(root.X, root.Y);
        return transform;
    }

    private void DrawHand(DrawingContext dc, SolArmPose pose, bool left, Matrix stage)
    {
        // Both hands relax to the same original mitten. The palm opens only for
        // a wave/celebration, at the same wrist and along the same finger axis.
        // The authored axis is continuous: no shortest-arc branch can spin a
        // partially blended hand when its forearm crosses an angle boundary.
        void DrawPose(bool open, double opacity)
        {
            if (opacity <= .001) return;
            var wrist = open ? new Point(943, 583) : new Point(346, 903);
            var sourceAxis = open ? -80.0 : 103.0;
            var mirror = open ? left : !left;
            var effectiveAxis = mirror ? 180 - sourceAxis : sourceAxis;
            var transform = Matrix.Identity;
            transform.Translate(-wrist.X, -wrist.Y);
            var scale = open ? .105 : .15;
            transform.Scale(mirror ? -scale : scale, scale);
            transform.Rotate(pose.FingerAxisDegrees - effectiveAxis);
            transform.Translate(pose.Limb.End.X, pose.Limb.End.Y);
            dc.PushOpacity(opacity);
            Raster(dc, open ? OpenHand : ClosedHand, transform, stage);
            dc.Pop();
        }
        // Change the finger pose during the fastest part of the arm gesture,
        // not over the whole two-second return. One base stays fully opaque
        // throughout the short handoff, preventing a see-through palm.
        var opening = Math.Clamp((pose.OpenAmount - .44) / .12, 0, 1);
        opening = opening * opening * (3 - 2 * opening);
        DrawPose(false, Math.Min(1, 2 * (1 - opening)));
        DrawPose(true, Math.Min(1, 2 * opening));
    }
    private static void DrawFace(DrawingContext dc, SolPerformanceFrame f, Matrix transform)
    {
        dc.PushTransform(new MatrixTransform(transform)); dc.PushClip(Visor); dc.DrawGeometry(FaceGlass, null, Visor);
        dc.DrawGeometry(Gradient("#70969ACE", "#18375591", "#00375591", vertical: true), null, Shape("M341,335 C491,235 683,209 750,260 L697,322 C570,296 431,351 328,424 Z"));
        dc.DrawEllipse(Radial("#403BBAE3", "#003BBAE3"), null, new Point(749, 506), 80, 84); dc.Pop();
        foreach (var eye in new[] { new Point(469, 475), new Point(673, 414) })
        {
            dc.PushTransform(new TranslateTransform(eye.X + f.GazeX * 14, eye.Y + f.GazeY * 9)); dc.PushTransform(new RotateTransform(-16));
            var height = (48 - (f.Act == SolAct.Build ? 19 * f.Energy : 0)) * Math.Clamp(f.EyeOpen, 0, 1);
            var curve = new StreamGeometry();
            using (var g = curve.Open())
            {
                g.BeginFigure(new Point(-51, 7), false, false); g.BezierTo(new Point(-30, 7 - height), new Point(30, 7 - height), new Point(51, 7), true, false);
            }
            dc.DrawGeometry(null, RoundPen(Solid("#2951D9FF"), 39), curve); dc.DrawGeometry(null, RoundPen(Solid("#B249DEFF"), 29), curve);
            dc.DrawGeometry(null, RoundPen(Cyan, 20), curve); dc.DrawGeometry(null, RoundPen(Solid("#72EEFFFF"), 6), curve);
            var browHeight = -63 + (f.Act == SolAct.Inspect ? f.Energy * (5 - f.GazeX * 5) : 0);
            var brow = new StreamGeometry();
            using (var g = brow.Open())
            {
                g.BeginFigure(new Point(-22, browHeight + 3), false, false); g.QuadraticBezierTo(new Point(0, browHeight - 6), new Point(22, browHeight), true, false);
            }
            dc.DrawGeometry(null, RoundPen(Solid("#C05FE5FF"), 5), brow); dc.Pop(); dc.Pop();
        }
        dc.PushOpacity(.32 + f.Energy * .18); dc.DrawGeometry(null, RoundPen(Cyan, 4), Shape("M553,528 Q576,535 593,516")); dc.Pop(); dc.Pop();
    }
    private static Point P(SolPoint p) => new(p.X, p.Y);
    private static SolPoint ToSol(Point p) => new(p.X, p.Y);
    private static Geometry Shape(string path) => Shapes.GetOrAdd(path, static key => { var shape = Geometry.Parse(key); shape.Freeze(); return shape; });
    private static SolidColorBrush Solid(string hex) => Solids.GetOrAdd(hex, static key => { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(key)); b.Freeze(); return b; });
    private static Brush Gradient(string a, string b, string c, bool vertical = false)
        => Gradients.GetOrAdd((a, b, c, vertical), static key =>
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = key.Item4 ? new Point(0, 1) : new Point(1, .22) };
        brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(key.Item1), 0));
        brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(key.Item2), .43));
        brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(key.Item3), 1)); brush.Freeze(); return brush;
    });
    private static Brush Radial(string a, string b)
        => Radials.GetOrAdd((a, b), static key => { var brush = new RadialGradientBrush((Color)ColorConverter.ConvertFromString(key.Item1), (Color)ColorConverter.ConvertFromString(key.Item2)); brush.Freeze(); return brush; });
    private static Pen RoundPen(Brush brush, double width) => new(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
    public void Dispose() { if (_disposed) return; StopMotion(); _disposed = true; }
}
