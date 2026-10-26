namespace CodexAccountMonitor.Infrastructure;

/// <summary>Orients raster fingers as a continuation of a connected forearm.</summary>
public static class SolHandPose
{
    /// <summary>
    /// Returns the rotation to apply after mirroring the source image around its
    /// wrist. Angles are clockwise in screen coordinates. At zero blend, fingers
    /// continue elbow-to-wrist; at one, they aim at the intentional gesture angle.
    /// The blend follows the shortest angular arc, including across ±180 degrees.
    /// </summary>
    public static double RotationDegrees(
        SolPoint elbow, SolPoint wrist, double sourceFingerAxisDegrees, bool mirrorX,
        double actFingerAxisDegrees, double actBlend)
    {
        ValidatePoint(elbow, nameof(elbow));
        ValidatePoint(wrist, nameof(wrist));
        ValidateFinite(sourceFingerAxisDegrees, nameof(sourceFingerAxisDegrees));
        ValidateFinite(actFingerAxisDegrees, nameof(actFingerAxisDegrees));
        ValidateFinite(actBlend, nameof(actBlend));

        var dx = wrist.X - elbow.X;
        var dy = wrist.Y - elbow.Y;
        if (!double.IsFinite(dx) || !double.IsFinite(dy))
        {
            // Direction is still defined when subtracting opposite finite extremes.
            dx = wrist.X * .5 - elbow.X * .5;
            dy = wrist.Y * .5 - elbow.Y * .5;
        }
        if (dx == 0 && dy == 0)
            throw new ArgumentException("The forearm must have distinct elbow and wrist anchors.", nameof(wrist));

        var forearmDegrees = Math.Atan2(dy, dx) * (180 / Math.PI);
        var gestureDelta = WrapDegrees(WrapDegrees(actFingerAxisDegrees) - forearmDegrees);
        var desiredFingerAxis = forearmDegrees + gestureDelta * Math.Clamp(actBlend, 0, 1);
        var sourceAxis = WrapDegrees(sourceFingerAxisDegrees);
        // A horizontal mirror maps a direction to 180 - angle, not -angle.
        var effectiveSourceAxis = mirrorX ? 180 - sourceAxis : sourceAxis;
        return desiredFingerAxis - effectiveSourceAxis;
    }

    private static double WrapDegrees(double degrees)
    {
        var wrapped = degrees % 360;
        if (wrapped > 180) wrapped -= 360;
        if (wrapped < -180) wrapped += 360;
        return wrapped;
    }

    private static void ValidatePoint(SolPoint point, string parameterName)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            throw new ArgumentOutOfRangeException(parameterName, "Point coordinates must be finite.");
    }

    private static void ValidateFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(parameterName, "The value must be finite.");
    }
}
