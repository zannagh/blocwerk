// <copyright file="HoldPositionRefiner.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>
/// Where the capture photos put a hold relative to its panel-photo silhouette. The panel silhouette is
/// mapped onto the facet through the panel photo's own fit (<see cref="HoldPlaneProjector"/>), which can
/// misplace a hold by a centimetre or two where the panel view compresses depth; the capture photos are
/// solved cameras. When the traced capture silhouettes AGREE on a shift (well beyond their scatter, each
/// view's protrusion smear fitted out, see <see cref="Shift"/>),
/// the footprint is built from the shifted panel silhouette instead (<see cref="HoldFootprintEstimator"/>),
/// so every view of the 3D wall draws the hold where the photos show it. The panel photo's own X/Y stay
/// as they are: they are right for that photo.
/// </summary>
public static class HoldPositionRefiner
{
    /// <summary>Capture silhouettes needed before a shift is trusted.</summary>
    public const int MinViews = 5;

    /// <summary>Smaller shifts are left alone (the silhouettes' own noise).</summary>
    public const double MinShiftMm = 5;

    /// <summary>
    /// Larger shifts are not applied (a neighbouring hold was traced, or a repeating pattern). On B3
    /// (2026-09-24) shifts of 5–12 mm moved 24 holds closer to where the photos show them and 6 away; above
    /// 12 mm it was a coin toss.
    /// </summary>
    public const double MaxShiftMm = 12;

    /// <summary>The shift may be at most this share of the hold's smaller extent (repeating pockets).</summary>
    public const double MaxShiftPerSize = 0.5;

    /// <summary>The shift must exceed its standard error this many times.</summary>
    public const double MinSignificance = 3;

    /// <summary>A silhouette whose centroid lies further than this from the panel one is a different hold.</summary>
    public const double MaxViewDistanceMm = 45;

    /// <summary>The views' per-mm displacements must differ at least this much to tell height from position.</summary>
    public const double MinParallaxSpread = 0.3;

    /// <summary>Upper bound of the fitted effective height (centroid smear per unit displacement), mm.</summary>
    public const double MaxEffectiveHeightMm = 60;

    /// <summary>
    /// The agreed shift (da, db) of <paramref name="primary"/>, or null when the views do not agree on one.
    /// Every silhouette is smeared away from its own camera by the hold's protrusion, so a centroid sits at
    /// P + s·t (P the hold's true centroid, t the per-mm displacement from that camera, s the unknown
    /// effective height, both fitted over the capture views); the panel silhouette should sit at
    /// P + s·t(panel camera), and the shift is what moves it there.
    /// </summary>
    /// <param name="frame">The hold's facet.</param>
    /// <param name="primary">The panel-photo silhouette on the facet.</param>
    /// <param name="others">The capture silhouettes on the facet.</param>
    /// <returns>The shift in plane mm, or null.</returns>
    public static (double A, double B)? Shift(FacetFrame frame, FootprintView primary, IReadOnlyList<FootprintView> others)
    {
        var area = PlanePolygon.Area(primary.Silhouette);
        if (primary.Silhouette.Count < 3 || area < 1 || PerMm(frame, (0, 0), primary.CameraMm) is null)
        {
            return null;
        }

        var c0 = Centroid(primary.Silhouette);
        var views = others
            .Where(v => v.Silhouette.Count >= 3)
            .Where(v => PlanePolygon.Area(v.Silhouette) is var a && a * HoldFootprintEstimator.MaxAreaRatio >= area
                        && a <= area * HoldFootprintEstimator.MaxAreaRatio)
            .Select(v => (C: Centroid(v.Silhouette), T: PerMm(frame, c0, v.CameraMm)))
            .Where(v => v.T is not null && Math.Sqrt(Math.Pow(v.C.A - c0.A, 2) + Math.Pow(v.C.B - c0.B, 2)) <= MaxViewDistanceMm)
            .Select(v => (v.C, T: v.T!.Value))
            .ToList();
        if (views.Count < MinViews || Spread(views.Select(v => v.T).ToList()) < MinParallaxSpread)
        {
            return null;
        }

        var (p, height, residuals) = Fit(views);
        var tp = PerMm(frame, c0, primary.CameraMm)!.Value;
        var shift = (A: p.A + (height * tp.A) - c0.A, B: p.B + (height * tp.B) - c0.B);
        var size = Math.Sqrt((shift.A * shift.A) + (shift.B * shift.B));
        var standardError = 1.25 * Median(residuals) / Math.Sqrt(views.Count);
        var (aMin, aMax) = PlanePolygon.Extent(primary.Silhouette, 1, 0);
        var (bMin, bMax) = PlanePolygon.Extent(primary.Silhouette, 0, 1);
        var limit = Math.Min(MaxShiftMm, MaxShiftPerSize * Math.Min(aMax - aMin, bMax - bMin));
        return size >= Math.Max(MinShiftMm, MinSignificance * standardError) && size <= limit ? shift : null;
    }

    /// <summary>The view moved by <paramref name="shift"/>.</summary>
    /// <param name="view">The view.</param>
    /// <param name="shift">The shift, plane mm.</param>
    /// <returns>The moved view.</returns>
    public static FootprintView Moved(FootprintView view, (double A, double B) shift) =>
        view with { Silhouette = view.Silhouette.Select(p => (p.A + shift.A, p.B + shift.B)).ToList() };

    /// <summary>Area centroid of a simple ring (vertex mean when degenerate).</summary>
    /// <param name="ring">The ring.</param>
    /// <returns>The centroid.</returns>
    public static (double A, double B) Centroid(IReadOnlyList<(double A, double B)> ring)
    {
        double a2 = 0, ca = 0, cb = 0;
        for (var i = 0; i < ring.Count; i++)
        {
            var (p, q) = (ring[i], ring[(i + 1) % ring.Count]);
            var cross = (p.A * q.B) - (q.A * p.B);
            a2 += cross;
            ca += (p.A + q.A) * cross;
            cb += (p.B + q.B) * cross;
        }

        return Math.Abs(a2) < 1e-9
            ? (ring.Average(p => p.A), ring.Average(p => p.B))
            : (ca / (3 * a2), cb / (3 * a2));
    }

    /// <summary>Robust least squares of c = P + s·t (s clamped to [0, <see cref="MaxEffectiveHeightMm"/>]).</summary>
    private static ((double A, double B) P, double S, List<double> Residuals) Fit(List<((double A, double B) C, (double A, double B) T)> views)
    {
        var w = views.Select(_ => 1.0).ToList();
        (double A, double B) p = (0, 0);
        double s = 0;
        var r = new List<double>();
        for (var iter = 0; iter < 4; iter++)
        {
            // normal equations of [1 0 ta; 0 1 tb]·(Pa, Pb, s) = (ca, cb), weighted
            double sw = 0, sta = 0, stb = 0, stt = 0, sca = 0, scb = 0, stc = 0;
            for (var i = 0; i < views.Count; i++)
            {
                var ((ca, cb), (ta, tb)) = views[i];
                sw += w[i];
                sta += w[i] * ta;
                stb += w[i] * tb;
                stt += w[i] * ((ta * ta) + (tb * tb));
                sca += w[i] * ca;
                scb += w[i] * cb;
                stc += w[i] * ((ta * ca) + (tb * cb));
            }

            var den = stt - (((sta * sta) + (stb * stb)) / sw);
            s = den > 1e-9 ? (stc - (((sta * sca) + (stb * scb)) / sw)) / den : 0;
            s = Math.Clamp(s, 0, MaxEffectiveHeightMm);
            p = ((sca - (s * sta)) / sw, (scb - (s * stb)) / sw);
            r = views.Select(v => Math.Sqrt(Math.Pow(v.C.A - p.A - (s * v.T.A), 2) + Math.Pow(v.C.B - p.B - (s * v.T.B), 2))).ToList();
            w = r.Select(x => 1 / Math.Max(x, 4.0)).ToList();
        }

        return (p, s, r);
    }

    /// <summary>Displacement per mm of height at plane point <paramref name="at"/> seen from <paramref name="camera"/>.</summary>
    private static (double A, double B)? PerMm(FacetFrame frame, (double A, double B) at, double[] camera)
    {
        var r = new[] { camera[0] - frame.Origin[0], camera[1] - frame.Origin[1], camera[2] - frame.Origin[2] };
        var d = (r[0] * frame.Normal[0]) + (r[1] * frame.Normal[1]) + (r[2] * frame.Normal[2]);
        if (d < 1)
        {
            return null;
        }

        var ca = (r[0] * frame.U[0]) + (r[1] * frame.U[1]) + (r[2] * frame.U[2]);
        var cb = (r[0] * frame.V[0]) + (r[1] * frame.V[1]) + (r[2] * frame.V[2]);
        return ((at.A - ca) / d, (at.B - cb) / d);
    }

    private static double Spread(List<(double A, double B)> t)
    {
        var best = 0.0;
        for (var i = 0; i < t.Count; i++)
        {
            for (var j = i + 1; j < t.Count; j++)
            {
                best = Math.Max(best, Math.Sqrt(Math.Pow(t[i].A - t[j].A, 2) + Math.Pow(t[i].B - t[j].B, 2)));
            }
        }

        return best;
    }

    private static double Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToList();
        return v.Count % 2 == 1 ? v[v.Count / 2] : 0.5 * (v[(v.Count / 2) - 1] + v[v.Count / 2]);
    }
}
