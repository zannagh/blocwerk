using System.Globalization;

namespace Blocwerk.Core.Helpers;

/// <summary>
/// Computes an "angle change wedge" — a triangular prism that sits on a wall at
/// <c>wallAngle</c> and presents a climbing face at a steeper <c>targetAngle</c>.
///
/// All angles are measured from horizontal, so 90° == vertical and a larger
/// angle == steeper. The cross-section is a single triangle: the face meets the
/// wall along one edge, runs out to the furthest corner, and a return (the
/// "lower portion") folds back to the wall. The return's length is implicit —
/// the face length and the two angles fix it — so at a 0° (horizontal) return
/// the piece comes out exactly face-sized.
///
/// Angles reported on the cut edges are the FOLD (included) angle between the
/// two adjoining panels. A 45° wall stepped up to a 90° (vertical) face folds
/// the face onto the wall by |90 - 45| = 45°; a 30° lower portion then folds the
/// face's far edge by |90 - 30| = 60° and folds onto the wall by |45 - 30| = 15°.
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

        if (targetAngleDeg <= 0)
        {
            throw new ArgumentException("Target angle must be greater than 0°.");
        }

        if (targetAngleDeg <= wallAngleDeg)
        {
            throw new ArgumentException("Target angle must be steeper than the wall angle.");
        }

        if (targetAngleDeg >= 180)
        {
            throw new ArgumentOutOfRangeException(nameof(targetAngleDeg), "Target angle must be below 180°.");
        }

        var hasLower = lowerPortionAngleDeg.HasValue;
        var p = lowerPortionAngleDeg ?? 0.0;

        if (hasLower && p <= 0)
        {
            throw new ArgumentException("Lower portion angle must be greater than 0°.");
        }

        if (p >= wallAngleDeg)
        {
            throw new ArgumentException("Lower portion angle must be shallower than the wall angle so it can fold back to the wall.");
        }

        var w = wallAngleDeg;
        var tgt = targetAngleDeg;
        var fh = faceHeight;

        // Interior angles of the cross-section triangle, in the wall's own frame
        // (wall along the x-axis, the volume above it):
        //   alpha = face rise above the wall      (T - W)
        //   beta  = return rise above the wall     (W - P)
        var alpha = Deg2Rad(tgt - w);
        var beta = Deg2Rad(w - p);

        // Triangle vertices in the wall frame. q & r sit on the wall; f is the
        // furthest corner. The return length falls straight out of the geometry.
        var q = new Point2D(0, 0);
        var f = new Point2D(fh * Math.Cos(alpha), fh * Math.Sin(alpha));
        var r = new Point2D(f.X + f.Y / Math.Tan(beta), 0);

        var lowerLength = fh * Math.Sin(alpha) / Math.Sin(beta);
        var wallFootprint = r.X;
        var depth = f.Y;

        var faceToWall = tgt - w;   // fold at q: face onto wall
        var faceToLower = tgt - p;  // fold at f: face onto the return
        var lowerToWall = w - p;    // fold at r: return onto wall

        var cross = new[] { q, f, r };
        var labels = new[] { "Face", hasLower ? "Lower" : "Base", "Wall" };
        var edgeLengths = new[] { Dist(q, f), Dist(f, r), Dist(r, q) };
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
            hasLower ? p : null,
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
