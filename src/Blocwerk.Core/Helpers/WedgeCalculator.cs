using System.Globalization;

namespace Blocwerk.Core.Helpers;

/// <summary>
/// Computes an "angle change wedge" — a triangular (or, with a lower portion,
/// quadrilateral) prism that sits on a wall at <c>wallAngle</c> and presents a
/// climbing face at a steeper <c>targetAngle</c>.
///
/// All angles are measured from horizontal, so 90° == vertical and a larger
/// angle == steeper. Angles reported on the cut edges are the FOLD (included)
/// angle between the two adjoining panels — i.e. the "angle change" at that
/// edge. For a 45° wall stepped up to a 90° (vertical) face the face's lower
/// edge folds by |90 - 45| = 45°; adding a 30° lower portion makes the face's
/// lower edge fold by |90 - 30| = 60° onto the lower portion.
/// </summary>
public static class WedgeCalculator
{
    public static WedgeResult Calculate(
        double wallAngleDeg,
        double targetAngleDeg,
        double faceWidth,
        double faceHeight,
        double thickness,
        double? lowerPortionAngleDeg = null,
        double lowerPortionLength = 0)
    {
        if (faceWidth <= 0 || faceHeight <= 0 || thickness <= 0)
        {
            throw new ArgumentException("Face size and thickness must be positive.");
        }

        if (wallAngleDeg <= 0 || wallAngleDeg >= 180)
        {
            throw new ArgumentOutOfRangeException(nameof(wallAngleDeg), "Wall angle must be between 0° and 180°.");
        }

        if (targetAngleDeg <= wallAngleDeg)
        {
            throw new ArgumentException("Target angle must be steeper than the wall angle.");
        }

        if (targetAngleDeg >= 180)
        {
            throw new ArgumentOutOfRangeException(nameof(targetAngleDeg), "Target angle must be below 180°.");
        }

        var w = wallAngleDeg;
        var tgt = targetAngleDeg;
        double? p = lowerPortionAngleDeg;

        if (p.HasValue)
        {
            if (p.Value <= 0 || p.Value >= tgt)
            {
                throw new ArgumentException("Lower portion angle must be greater than 0° and shallower than the target angle.");
            }

            if (lowerPortionLength <= 0)
            {
                throw new ArgumentException("Lower portion length must be positive.");
            }
        }

        // Cross-section (side profile) in world coordinates: x = outward from the
        // wall (toward the climber), y = up. Each surface at angle θ climbs along
        // the unit vector (-cos θ, sin θ), so a vertical (90°) surface climbs (0, 1).
        var cross = new List<Point2D>();
        var labels = new List<string>();
        var surfaceAngles = new List<double>();

        var pieces = new List<WedgePiece>();

        if (!p.HasValue)
        {
            // Triangular prism: wall -> face -> horizontal cap.
            var a = new Point2D(0, 0);                          // face bottom, on wall
            var b = Along(a, tgt, faceHeight);                  // face top
            var c = CapToWall(b, w);                            // cap back to wall

            cross.AddRange(new[] { a, b, c });
            labels.AddRange(new[] { "Face", "Cap", "Wall" });
            surfaceAngles.AddRange(new[] { tgt, 0.0, w });

            var faceToWall = Math.Abs(tgt - w);
            var faceToCap = tgt;
            var capToWall = w;

            pieces.Add(Panel("Face", faceHeight, faceWidth, faceToWall, faceToCap));
            pieces.Add(Panel("Cap (top)", Dist(b, c), faceWidth, faceToCap, capToWall));
            pieces.Add(EndPanel("Side (end)", cross));
        }
        else
        {
            // Quadrilateral prism (two wedges merged): wall -> lower portion -> face -> cap.
            var lp = p.Value;
            var a = new Point2D(0, 0);                          // lower portion bottom, on wall
            var d = Along(a, lp, lowerPortionLength);           // bend: lower portion -> face
            var b = Along(d, tgt, faceHeight);                  // face top
            var c = CapToWall(b, w);                            // cap back to wall

            cross.AddRange(new[] { a, d, b, c });
            labels.AddRange(new[] { "Lower", "Face", "Cap", "Wall" });
            surfaceAngles.AddRange(new[] { lp, tgt, 0.0, w });

            var lowerToWall = Math.Abs(lp - w);
            var faceToLower = Math.Abs(tgt - lp);
            var faceToCap = tgt;
            var capToWall = w;

            pieces.Add(Panel("Face", faceHeight, faceWidth, faceToLower, faceToCap));
            pieces.Add(Panel("Lower portion", lowerPortionLength, faceWidth, lowerToWall, faceToLower));
            pieces.Add(Panel("Cap (top)", Dist(b, c), faceWidth, faceToCap, capToWall));
            pieces.Add(EndPanel("Side (end)", cross));
        }

        var crossArr = cross.ToArray();
        var edgeLengths = new double[crossArr.Length];
        var edgeAngles = new double[crossArr.Length];
        for (var i = 0; i < crossArr.Length; i++)
        {
            var next = crossArr[(i + 1) % crossArr.Length];
            edgeLengths[i] = Dist(crossArr[i], next);
        }

        // Fold angle stored at each vertex = |surface_in - surface_out|, i.e. the
        // included fold between the two panels meeting at that corner.
        for (var i = 0; i < crossArr.Length; i++)
        {
            var prev = surfaceAngles[(i - 1 + surfaceAngles.Count) % surfaceAngles.Count];
            var curr = surfaceAngles[i];
            edgeAngles[i] = Math.Abs(curr - prev);
        }

        var wallDir = Along(new Point2D(0, 0), w, 1);
        var normalX = Math.Sin(Deg2Rad(w));
        var normalY = Math.Cos(Deg2Rad(w));
        var depth = crossArr.Max(v => v.X * normalX + v.Y * normalY);
        var wallFootprint = crossArr.Max(v => v.X * wallDir.X + v.Y * wallDir.Y)
                          - crossArr.Min(v => v.X * wallDir.X + v.Y * wallDir.Y);

        return new WedgeResult(
            pieces,
            crossArr,
            labels.ToArray(),
            edgeLengths,
            edgeAngles,
            Math.Abs(tgt - w),
            Math.Abs(tgt - w),
            p.HasValue ? Math.Abs(tgt - p.Value) : null,
            p.HasValue ? Math.Abs(p.Value - w) : null,
            depth,
            wallFootprint,
            faceWidth + 2.0 * thickness);
    }

    public static string F(double v) => v.ToString("F1", CultureInfo.InvariantCulture);

    public static string F2(double v) => v.ToString("F2", CultureInfo.InvariantCulture);

    private static WedgePiece Panel(string name, double length, double width, double bottomFold, double topFold)
    {
        // Rectangle laid out flat: x = board width, y = board length.
        // Bottom & top edges are the cross-cuts that carry the fold bevels; the
        // two long side edges are square (they meet the end panels at 90°).
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

    private static WedgePiece EndPanel(string name, List<Point2D> cross)
    {
        var verts = cross.ToArray();
        var n = verts.Length;
        var lengths = new double[n];
        var angles = new double[n];
        for (var i = 0; i < n; i++)
        {
            var next = verts[(i + 1) % n];
            lengths[i] = Dist(verts[i], next);
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

    private static Point2D Along(Point2D from, double angleDeg, double length)
    {
        var r = Deg2Rad(angleDeg);
        return new Point2D(from.X - length * Math.Cos(r), from.Y + length * Math.Sin(r));
    }

    private static Point2D CapToWall(Point2D b, double wallAngleDeg)
    {
        // Horizontal cap from the face top back to the wall line (through origin,
        // direction (-cos w, sin w)). Intersect y = b.Y with the wall.
        var r = Deg2Rad(wallAngleDeg);
        var s = b.Y / Math.Sin(r);
        var x = -s * Math.Cos(r);
        return new Point2D(x, b.Y);
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
