using System.Globalization;

namespace Blocwerk.Core.Helpers;

/// <summary>
/// Computes an "angle change wedge" — a triangular prism that sits on a wall at
/// <c>wallAngle</c> and presents a climbing face at a different <c>targetAngle</c>.
///
/// All three angles (wall, face/target and the lower portion) are absolute, measured
/// from horizontal: 0° == horizontal, 90° == vertical, larger == steeper/more
/// overhung. The cross-section is a single triangle whose base sits on the wall; the
/// face runs from one wall attachment out to the tip, and the lower portion returns
/// from the tip back to the wall.
///
/// For the triangle to close (the lower portion actually returns to the wall) the
/// face and the lower portion must sit on OPPOSITE sides of the wall angle — one
/// steeper than the wall, the other shallower. If both are steeper (or both
/// shallower) the panels diverge and never meet the wall again, which is rejected.
///
/// Fold angles reported on the cut edges are the included angle between the two
/// adjoining panels, i.e. the absolute difference of the two surface angles: the
/// face folds onto the wall by |target − wall|, onto the lower portion (at the tip)
/// by |target − lower|, and the lower portion folds onto the wall by |lower − wall|.
/// </summary>
public static class WedgeCalculator
{
    public static WedgeResult Calculate(
        double wallAngleDeg,
        double targetAngleDeg,
        double faceWidth,
        double faceHeight,
        double thickness,
        double? lowerPortionAngleDeg = null)
    {
        if (faceWidth <= 0 || faceHeight <= 0 || thickness <= 0)
        {
            throw new ArgumentException("Face size and thickness must be positive.");
        }

        if (wallAngleDeg <= 0 || wallAngleDeg >= 180)
        {
            throw new ArgumentOutOfRangeException(nameof(wallAngleDeg), "Wall angle must be between 0° and 180°.");
        }

        if (targetAngleDeg <= 0 || targetAngleDeg >= 180)
        {
            throw new ArgumentOutOfRangeException(nameof(targetAngleDeg), "Resulting face angle must be between 0° and 180°.");
        }

        if (Math.Abs(targetAngleDeg - wallAngleDeg) < 1e-6)
        {
            throw new ArgumentException("Resulting face angle must differ from the wall angle.");
        }

        var hasLower = lowerPortionAngleDeg.HasValue;
        var lower = lowerPortionAngleDeg ?? 0.0;

        if (lower < 0 || lower >= 180)
        {
            throw new ArgumentException("Lower portion angle must be between 0° and 180°.");
        }

        if (Math.Abs(lower - wallAngleDeg) < 1e-6)
        {
            throw new ArgumentException("Lower portion angle must differ from the wall angle so it can fold back to it.");
        }

        var w = wallAngleDeg;
        var tgt = targetAngleDeg;
        var fh = faceHeight;

        // Face and lower portion, measured relative to the wall (positive == steeper
        // than the wall, negative == shallower). They must straddle the wall for the
        // triangle to close.
        var phiFace = Deg2Rad(tgt - w);
        var phiLower = Deg2Rad(lower - w);

        if (Math.Sign(tgt - w) == Math.Sign(lower - w))
        {
            throw new ArgumentException(
                "The resulting face and the lower portion must be on opposite sides of the wall angle " +
                "(one steeper, one shallower); otherwise the lower portion never returns to the wall.");
        }

        // Cross-section in the wall's own frame: the wall is the x-axis, +y points
        // out into the room. The tip sits at perpendicular distance `depth` from the
        // wall; the face and lower panels drop from the tip to the wall (y = 0).
        var depth = fh * Math.Abs(Math.Sin(phiFace));
        var sgn = Math.Sin(phiFace) >= 0 ? 1.0 : -1.0;

        var a = new Point2D(0, 0);                                                // face meets wall
        var b = new Point2D(sgn * fh * Math.Cos(phiFace), depth);                 // tip
        var c = new Point2D(b.X - depth / Math.Tan(phiLower), 0);                 // lower meets wall

        var lowerLength = depth / Math.Abs(Math.Sin(phiLower));
        var wallFootprint = Math.Abs(c.X - a.X);

        var faceToWall = Math.Abs(tgt - w);    // fold at a: face onto wall
        var faceToLower = Math.Abs(tgt - lower); // fold at b: face onto the return
        var lowerToWall = Math.Abs(lower - w);  // fold at c: return onto wall

        var cross = new[] { a, b, c };
        var labels = new[] { "Face", hasLower ? "Lower" : "Base", "Wall" };
        var edgeLengths = new[] { Dist(a, b), Dist(b, c), Dist(c, a) };
        var edgeAngles = new[] { faceToWall, faceToLower, lowerToWall };

        var pieces = new List<WedgePiece>
        {
            Panel("Face", fh, faceWidth, faceToWall, faceToLower),
            Panel(hasLower ? "Lower portion" : "Base (return)", lowerLength, faceWidth, lowerToWall, faceToLower),
            EndPanel("Side (end)", cross),
        };

        return new WedgeResult(
            pieces,
            cross,
            labels,
            edgeLengths,
            edgeAngles,
            tgt - w,
            hasLower ? lower : null,
            lowerLength,
            hasLower ? faceToLower : null,
            hasLower ? lowerToWall : null,
            depth,
            wallFootprint,
            faceWidth + 2.0 * thickness);
    }

    public static string F(double v) => v.ToString("F1", CultureInfo.InvariantCulture);

    public static string F2(double v) => v.ToString("F2", CultureInfo.InvariantCulture);

    private static WedgePiece Panel(string name, double length, double width, double bottomFold, double topFold)
    {
        // Rectangle laid out flat: x = board width, y = board length. The two
        // cross-cut ends carry the fold bevels; the long side edges are square
        // (they meet the end panels at 90°).
        var verts = new Point2D[]
        {
            new(0, 0),
            new(width, 0),
            new(width, length),
            new(0, length),
        };

        var lengths = new[] { width, length, width, length };
        var bevels = new[] { bottomFold, 90.0, topFold, 90.0 };
        return new WedgePiece(name, 1, verts, bevels, lengths);
    }

    private static WedgePiece EndPanel(string name, IReadOnlyList<Point2D> cross)
    {
        var verts = cross.ToArray();
        var n = verts.Length;
        var lengths = new double[n];
        var angles = new double[n];
        for (var i = 0; i < n; i++)
        {
            lengths[i] = Dist(verts[i], verts[(i + 1) % n]);
        }

        // Interior angle at each vertex, for marking out the profile on the sheet.
        for (var i = 0; i < n; i++)
        {
            var prev = verts[(i - 1 + n) % n];
            var curr = verts[i];
            var next = verts[(i + 1) % n];
            angles[i] = InteriorAngle(prev, curr, next);
        }

        return new WedgePiece(name, 2, verts, angles, lengths);
    }

    private static double Dist(Point2D a, Point2D b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static double InteriorAngle(Point2D prev, Point2D curr, Point2D next)
    {
        var v1x = prev.X - curr.X;
        var v1y = prev.Y - curr.Y;
        var v2x = next.X - curr.X;
        var v2y = next.Y - curr.Y;
        var dot = v1x * v2x + v1y * v2y;
        var l1 = Math.Sqrt(v1x * v1x + v1y * v1y);
        var l2 = Math.Sqrt(v2x * v2x + v2y * v2y);
        if (l1 == 0 || l2 == 0)
        {
            return 0;
        }

        var cos = Math.Clamp(dot / (l1 * l2), -1.0, 1.0);
        return Rad2Deg(Math.Acos(cos));
    }

    private static double Rad2Deg(double rad) => rad * 180.0 / Math.PI;

    private static double Deg2Rad(double deg) => deg * Math.PI / 180.0;
}
