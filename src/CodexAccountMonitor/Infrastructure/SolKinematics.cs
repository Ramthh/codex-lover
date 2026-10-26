namespace CodexAccountMonitor.Infrastructure;

public readonly record struct SolLimb(SolPoint Root, SolPoint Joint, SolPoint End);

/// <summary>Deterministic two-bone geometry in the character's local coordinates.</summary>
public static class SolKinematics
{
    /// <summary>
    /// Solves a limb while preserving both bone lengths. Unreachable targets are
    /// clamped to the nearest reachable radius. Positive bend uses the perpendicular
    /// (-Y, X), so it bends downward for a target to the right in screen coordinates.
    /// </summary>
    public static SolLimb Solve(SolPoint root, SolPoint target, double upperLength, double lowerLength, int bendDirection)
    {
        ValidatePoint(root, nameof(root));
        ValidatePoint(target, nameof(target));
        ValidateLength(upperLength, nameof(upperLength));
        ValidateLength(lowerLength, nameof(lowerLength));
        if (bendDirection is not (-1 or 1))
            throw new ArgumentOutOfRangeException(nameof(bendDirection), "Bend direction must be -1 or 1.");

        var maximumReach = upperLength + lowerLength;
        if (!double.IsFinite(maximumReach))
            throw new ArgumentOutOfRangeException(nameof(upperLength), "The combined bone length must be finite.");
        var minimumReach = Math.Abs(upperLength - lowerLength);

        var dx = target.X - root.X;
        var dy = target.Y - root.Y;
        // Opposite finite coordinates can overflow subtraction. Halving both points
        // preserves their direction; an infinite distance simply means unreachable.
        var distanceMultiplier = 1.0;
        if (!double.IsFinite(dx) || !double.IsFinite(dy))
        {
            dx = target.X * .5 - root.X * .5;
            dy = target.Y * .5 - root.Y * .5;
            distanceMultiplier = 2;
        }

        var componentScale = Math.Max(Math.Abs(dx), Math.Abs(dy));
        var directionX = 1.0;
        var directionY = 0.0;
        var distance = 0.0;
        if (componentScale > 0)
        {
            var scaledX = dx / componentScale;
            var scaledY = dy / componentScale;
            var scaledDistance = Math.Sqrt(scaledX * scaledX + scaledY * scaledY);
            directionX = scaledX / scaledDistance;
            directionY = scaledY / scaledDistance;
            distance = componentScale * scaledDistance * distanceMultiplier;
        }

        var reach = Math.Clamp(distance, minimumReach, maximumReach);
        double along;
        double height;
        if (reach == 0)
        {
            // Coincident anchors with equal bones fold on a deterministic side.
            along = 0;
            height = upperLength;
        }
        else if (reach == maximumReach)
        {
            along = upperLength;
            height = 0;
        }
        else if (reach == minimumReach)
        {
            along = upperLength >= lowerLength ? upperLength : -upperLength;
            height = 0;
        }
        else
        {
            if (upperLength == lowerLength)
            {
                // Avoid dividing by a vanishing distance in the folded limit.
                along = reach * .5;
            }
            else
            {
                var lengthScale = Math.Max(upperLength, lowerLength);
                var upper = upperLength / lengthScale;
                var lower = lowerLength / lengthScale;
                var span = reach / lengthScale;
                along = lengthScale * (((upper - lower) * (upper + lower) / span + span) * .5);
            }
            var cosine = Math.Clamp(along / upperLength, -1, 1);
            along = upperLength * cosine;
            height = upperLength * Math.Sqrt((1 - cosine) * (1 + cosine));
        }

        var joint = new SolPoint(
            root.X + directionX * along - directionY * height * bendDirection,
            root.Y + directionY * along + directionX * height * bendDirection);
        // Preserve the exact requested endpoint when it is reachable.
        var end = distance >= minimumReach && distance <= maximumReach
            ? target
            : new SolPoint(root.X + directionX * reach, root.Y + directionY * reach);
        ValidateResult(joint, nameof(root));
        ValidateResult(end, nameof(root));
        return new SolLimb(root, joint, end);
    }

    /// <summary>Rotates a point around its pivot; positive angles turn clockwise on screen.</summary>
    public static SolPoint Rotate(SolPoint point, SolPoint pivot, double degrees)
    {
        ValidatePoint(point, nameof(point));
        ValidatePoint(pivot, nameof(pivot));
        if (!double.IsFinite(degrees))
            throw new ArgumentOutOfRangeException(nameof(degrees), "The angle must be finite.");
        var wrappedDegrees = degrees % 360;
        if (wrappedDegrees == 0)
            return point;

        var radians = wrappedDegrees * (Math.PI / 180);
        var (sine, cosine) = Math.SinCos(radians);
        var dx = point.X - pivot.X;
        var dy = point.Y - pivot.Y;
        var result = new SolPoint(
            pivot.X + dx * cosine - dy * sine,
            pivot.Y + dx * sine + dy * cosine);
        ValidateResult(result, nameof(point));
        return result;
    }

    private static void ValidatePoint(SolPoint point, string parameterName)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            throw new ArgumentOutOfRangeException(parameterName, "Point coordinates must be finite.");
    }

    private static void ValidateLength(double length, string parameterName)
    {
        if (!double.IsFinite(length) || length <= 0)
            throw new ArgumentOutOfRangeException(parameterName, "Bone lengths must be positive and finite.");
    }

    private static void ValidateResult(SolPoint point, string parameterName)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            throw new ArgumentOutOfRangeException(parameterName, "The geometry exceeds the finite coordinate range.");
    }
}
