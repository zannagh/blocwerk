using Blocwerk.Core.Abstractions;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Markers;

/// <summary>
/// Edge-line corner refinement, ported from tools/glyph/geometry/refine.py. The markers are screwed
/// to the wall AT their corners, and ArUco's corners get pulled onto the screw heads, the white
/// paper edge or into the black square (3–29 px on the wall photos). This refits each side of the
/// black square as a line from edge samples taken away from the corners (15..85 % of the side) and
/// intersects adjacent lines. A corner is only replaced when both adjacent lines fit and the
/// intersection stays within a quarter side of the original; otherwise the original is kept and
/// the reason reported. Synthetic (reconstructed) and off-image corners are always kept.
/// </summary>
public static class MarkerCornerRefiner
{
    /// <summary>
    /// Below this mean side the ±3 px minimum profile reaches past the black border (side/6) into the
    /// data cells, so the edge samples would be meaningless.
    /// </summary>
    public const double MinSidePx = 18.0;

    /// <summary>A corner may move at most this fraction of the mean side.</summary>
    private const double MaxShiftFraction = 0.25;

    /// <summary>Most refinement passes per marker (the first one plus up to three re-fits).</summary>
    private const int MaxPasses = 4;

    /// <summary>
    /// Corners that move less than this in a pass have settled, px. The edge fits cycle within about
    /// 0.02–0.4 px on real photos, so a tighter threshold just means "always MaxPasses".
    /// </summary>
    private const double ConvergedPx = 0.15;

    /// <summary>Refines one marker's corners (TL, TR, BR, BL, full-resolution px) in <paramref name="gray"/>.</summary>
    /// <param name="gray">The single-channel image the corners were detected in (not pre-blurred).</param>
    /// <param name="corners">The four detected corners.</param>
    /// <param name="syntheticCorners">Optional per-corner flags: true = reconstructed, never refine.</param>
    /// <param name="method">Sub-pixel edge locator; <see cref="EdgeSubPixelMethod.Parabola"/> reproduces refine.py exactly.</param>
    public static MarkerCornerRefinement Refine(
        Mat gray,
        IReadOnlyList<MarkerPoint> corners,
        IReadOnlyList<bool>? syntheticCorners = null,
        EdgeSubPixelMethod method = EdgeSubPixelMethod.PeakCentroid)
    {
        ArgumentNullException.ThrowIfNull(gray);
        ArgumentNullException.ThrowIfNull(corners);
        if (corners.Count != 4 || gray.Channels() != 1)
        {
            throw new ArgumentException("Expected 4 corners and a single-channel image.");
        }

        var c = corners.Select(p => new Point2d(p.X, p.Y)).ToArray();
        var side = MeanSide(c);
        var status = new CornerRefinementStatus[4];
        for (var i = 0; i < 4; i++)
        {
            status[i] = PreCheck(c[i], syntheticCorners?[i] == true, side, gray.Width, gray.Height);
        }

        var sampler = status.Any(s => s == CornerRefinementStatus.Refined)
            ? MarkerEdgeSampler.Create(gray, c, side)
            : null;
        var lines = sampler is null ? new EdgeLine?[4] : FitSides(sampler, c, side, method);
        var output = (Point2d[])c.Clone();
        for (var i = 0; i < 4; i++)
        {
            if (status[i] == CornerRefinementStatus.Refined)
            {
                status[i] = Intersect(lines[(i + 3) % 4], lines[i], c[i], side, out output[i]);
            }
        }

        return new MarkerCornerRefinement(
            output.Select(p => new MarkerPoint(p.X, p.Y)).ToList(),
            status,
            output.Select((p, i) => Distance(p, c[i])).ToList(),
            Residual(lines));
    }

    /// <summary>
    /// Refines every marker of a detection result. Markers flagged <see cref="DetectedMarker.Synthetic"/>
    /// carry no per-corner information, so all their corners are kept.
    /// </summary>
    public static IReadOnlyList<DetectedMarker> RefineAll(Mat gray, IReadOnlyList<DetectedMarker> markers)
    {
        ArgumentNullException.ThrowIfNull(markers);
        return markers.Select(m => m.Synthetic ? m : Apply(m, RefineConverged(gray, m.CornersPx), gray.Width, gray.Height)).ToList();
    }

    /// <summary>
    /// Re-runs <see cref="Refine"/> from its own output until the corners settle. One pass samples the
    /// sides along the detector's quad, which can sit on the paper edge or a screw head; the next pass
    /// samples along the fitted square, so the result no longer depends on which quad ArUco returned
    /// (on the wall photos a single pass differed by up to 1.4 px between two detector settings, the
    /// converged corners by under 0.2 px). A corner only moves while it stays within
    /// <see cref="MaxShiftFraction"/> of a side from the detected one.
    /// </summary>
    public static MarkerCornerRefinement RefineConverged(Mat gray, IReadOnlyList<MarkerPoint> corners)
    {
        var first = Refine(gray, corners);
        var current = first.Corners;
        var residual = first.ResidualPx;
        var side = MeanSide(corners.Select(p => new Point2d(p.X, p.Y)).ToArray());
        for (var pass = 1; pass < MaxPasses && first.RefinedCount > 0; pass++)
        {
            var next = Refine(gray, current);
            var moved = 0.0;
            var updated = current.ToArray();
            for (var i = 0; i < 4; i++)
            {
                if (first.Status[i] == CornerRefinementStatus.Refined
                    && next.Status[i] == CornerRefinementStatus.Refined
                    && Distance(next.Corners[i], corners[i]) <= MaxShiftFraction * side)
                {
                    moved = Math.Max(moved, Distance(next.Corners[i], current[i]));
                    updated[i] = next.Corners[i];
                }
            }

            current = updated;
            residual = next.ResidualPx;
            if (moved < ConvergedPx)
            {
                break;
            }
        }

        return first with { Corners = current, ResidualPx = residual, ShiftPx = current.Select((p, i) => Distance(p, corners[i])).ToList() };
    }

    private static DetectedMarker Apply(DetectedMarker marker, MarkerCornerRefinement refinement, int width, int height)
    {
        if (refinement.RefinedCount == 0)
        {
            return marker;
        }

        var corners = refinement.Corners;
        var edges = Enumerable.Range(0, 4).Select(i => Distance(corners[i], corners[(i + 1) % 4])).ToArray();
        return marker with
        {
            CornersPx = corners,
            CornersNormalized = corners.Select(p => new MarkerPoint(p.X / width, p.Y / height)).ToList(),
            SidePx = edges.Average(),
            EdgeRatio = edges.Max() / Math.Max(edges.Min(), 1e-9),
        };
    }

    private static CornerRefinementStatus PreCheck(Point2d p, bool synthetic, double side, int width, int height)
    {
        if (synthetic)
        {
            return CornerRefinementStatus.KeptSynthetic;
        }

        if (p.X < 0 || p.Y < 0 || p.X > width - 1 || p.Y > height - 1)
        {
            return CornerRefinementStatus.KeptOutsideImage;
        }

        return side < MinSidePx ? CornerRefinementStatus.KeptMarkerTooSmall : CornerRefinementStatus.Refined;
    }

    /// <summary>Side i runs from corner i to corner i+1.</summary>
    private static EdgeLine?[] FitSides(MarkerEdgeSampler sampler, Point2d[] c, double side, EdgeSubPixelMethod method)
    {
        var centre = new Point2d(c.Average(p => p.X), c.Average(p => p.Y));
        var lines = new EdgeLine?[4];
        for (var i = 0; i < 4; i++)
        {
            lines[i] = EdgeLineFitter.Fit(sampler.EdgePoints(c[i], c[(i + 1) % 4], centre, side, method));
        }

        return lines;
    }

    /// <summary>Corner i is the intersection of side (i-1 → i) and side (i → i+1).</summary>
    private static CornerRefinementStatus Intersect(EdgeLine? before, EdgeLine? after, Point2d original, double side, out Point2d result)
    {
        result = original;
        if (before is null || after is null)
        {
            return CornerRefinementStatus.KeptEdgeFitFailed;
        }

        if (EdgeLineFitter.Intersect(before.Value, after.Value) is not { } p)
        {
            return CornerRefinementStatus.KeptParallelSides;
        }

        if (Distance(p, original) > MaxShiftFraction * side)
        {
            return CornerRefinementStatus.KeptShiftTooLarge;
        }

        result = p;
        return CornerRefinementStatus.Refined;
    }

    private static double? Residual(EdgeLine?[] lines)
    {
        var fitted = lines.Where(l => l is not null).Select(l => l!.Value).ToList();
        var n = fitted.Sum(l => l.InlierCount);
        return n == 0 ? null : Math.Sqrt(fitted.Sum(l => l.SumSquaredResidual) / n);
    }

    private static double MeanSide(Point2d[] c) =>
        Enumerable.Range(0, 4).Average(i => Distance(c[i], c[(i + 1) % 4]));

    private static double Distance(Point2d a, Point2d b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    private static double Distance(MarkerPoint a, MarkerPoint b) => Distance(new Point2d(a.X, a.Y), new Point2d(b.X, b.Y));
}
