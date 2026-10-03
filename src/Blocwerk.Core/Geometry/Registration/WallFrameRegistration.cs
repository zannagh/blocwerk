// <copyright file="WallFrameRegistration.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.Registration;

/// <summary>
/// Ties a freshly solved wall model to the wall's active one, so hold positions (facet + plane mm) stay
/// valid across captures. The solver picks its frame from what it saw (origin = the reference facet's
/// markers' bounding box), so a new marker, a missing photo or another reference facet moves it. This fits
/// the rigid transform new → reference from the corners of markers that are the SAME physical marker in
/// both (unchanged between the two plan revisions), robustly: a marker that disagrees is dropped. It refuses
/// with fewer than <see cref="MinMarkers"/> markers, markers along one line, or markers that disagree.
/// <para>
/// Two real solves of the same, unchanged wall do not agree exactly: a marker seen in one or two photos is
/// placed 10–30 mm differently from solve to solve, one seen in five or more within a few mm, and dropping a
/// few markers from a solve shifts the rest by up to ~10 mm (the facets' relative angles move by tenths of a
/// degree). Scale agrees to 0.05 % and corners agree as well as centres, so the fit stays rigid and on
/// corners, but weights each marker by how well BOTH solves pinned it down: w = 1 / (1/n_ref + 1/n_new), n
/// being the number of photos it was seen in (the inverse of its variance when that falls as 1/n).
/// </para>
/// <para>
/// Before its residuals are judged, the new solve itself must be sound: if the solver flagged a marker that changed in this
/// revision (typically a size declared wrong), or more than a fifth of the used markers, it is refused — a
/// misdeclared size distorts every marker, and the fit can still look acceptable.
/// </para>
/// </summary>
public static class WallFrameRegistration
{
    /// <summary>The least markers a registration needs.</summary>
    public const int MinMarkers = 3;

    /// <summary>A marker whose corners miss by more than this (RMS, mm) after the fit is dropped.</summary>
    public const double OutlierMm = 25;

    /// <summary>
    /// The fit is refused when the kept markers still miss by more than this (observation-weighted RMS, mm).
    /// Re-solves of an unchanged wall land at 6–10 mm, broken solves at 18 mm and more.
    /// </summary>
    public const double MaxRmsMm = 15;

    /// <summary>
    /// Also refused above this plain (unweighted) RMS, mm: weighting may discount poorly seen markers, but not
    /// hide that most of them disagree.
    /// </summary>
    public const double MaxPlainRmsMm = 20;

    /// <summary>The kept marker centres must spread at least this far (mm, standard deviation) in two directions.</summary>
    public const double MinSpreadMm = 150;

    /// <summary>
    /// Fits <paramref name="solved"/> → <paramref name="reference"/>. <paramref name="eligibleIds"/> are the
    /// ids unchanged between the two revisions (null = every shared id).
    /// </summary>
    public static FrameRegistrationResult Register(
        WallGeometryDocument reference, WallGeometryDocument solved, IReadOnlySet<int>? eligibleIds)
    {
        var refCorners = MarkerWorldCorners.Of(reference);
        var newCorners = MarkerWorldCorners.Of(solved);
        var shared = newCorners.Keys.Where(refCorners.ContainsKey).Order().ToList();
        var changed = eligibleIds is null ? [] : shared.Where(id => !eligibleIds.Contains(id)).ToList();
        var used = shared.Where(id => eligibleIds is null || eligibleIds.Contains(id)).ToList();
        if (used.Count < MinMarkers)
        {
            return Refuse(TooFewMessage(used), used, changed, []);
        }

        var outliers = new List<int>();
        var fit = FitRobust(used, refCorners, newCorners, outliers);
        var kept = used.Except(outliers).ToList();
        var weights = Weights(reference, solved, kept);
        fit = fit is null ? null : FitWeighted(kept, refCorners, newCorners, weights);
        if (fit is null || kept.Count < MinMarkers)
        {
            return Refuse(TooFewMessage(kept) + OutlierNote(outliers), kept, changed, outliers);
        }

        var spread = Spread(kept.Select(id => Vec3.Mean(refCorners[id])).ToList());
        if (spread < MinSpreadMm)
        {
            return Refuse(
                $"The {kept.Count} unchanged markers sit almost in one line ({spread:0} mm spread), so the new model's "
                + "orientation cannot be pinned down. Keep unchanged markers spread out over the wall when changing markers.",
                kept, changed, outliers);
        }

        // After the geometric checks, before judging the residuals: a fit can look fine on a distorted solve.
        if (RegistrationRefusal.SolveIntegrity(solved, used, eligibleIds) is { } distorted)
        {
            return Refuse(distorted, kept, changed, outliers);
        }

        var residuals = kept.ToDictionary(id => id, id => Residual(fit, newCorners[id], refCorners[id]));
        var rms = Math.Sqrt(kept.Sum(id => weights[id] * residuals[id] * residuals[id]) / kept.Sum(id => weights[id]));
        var plainRms = Math.Sqrt(residuals.Values.Average(r => r * r));
        if (rms > MaxRmsMm || plainRms > MaxPlainRmsMm)
        {
            var message = RegistrationRefusal.Disagreement(rms, plainRms, residuals, solved, reference) + OutlierNote(outliers);
            return Refuse(message, kept, changed, outliers) with { RmsMm = rms, MaxMm = residuals.Values.Max(), ResidualsMm = residuals };
        }

        return new FrameRegistrationResult(true, fit, kept, changed, outliers, rms, residuals.Values.Max(), null, residuals);
    }

    /// <summary>World corners of every marker with a full facet frame (see <see cref="MarkerWorldCorners"/>).</summary>
    public static IReadOnlyDictionary<int, double[][]> WorldCorners(WallGeometryDocument document) => MarkerWorldCorners.Of(document);

    /// <summary>Marker id → photos it was seen in, at least 1 (documents without counts weigh every marker alike).</summary>
    internal static Dictionary<int, int> Observations(WallGeometryDocument document) =>
        document.Markers.GroupBy(m => m.Id).ToDictionary(g => g.Key, g => Math.Max(1, g.First().Observations));

    private static RigidTransform3D? FitRobust(
        List<int> ids, IReadOnlyDictionary<int, double[][]> reference, IReadOnlyDictionary<int, double[][]> solved, List<int> outliers)
    {
        var active = ids.ToList();
        while (true)
        {
            var pairs = active.SelectMany(id => solved[id].Zip(reference[id], (f, t) => (f, t))).ToList();
            var fit = RigidTransform3D.Fit(pairs);
            if (fit is null)
            {
                return null;
            }

            var residuals = active.Select(id => (Id: id, Mm: Residual(fit, solved[id], reference[id]))).ToList();
            var median = residuals.Select(r => r.Mm).Order().ElementAt(residuals.Count / 2);
            var worst = residuals.MaxBy(r => r.Mm);
            if (worst.Mm <= Math.Max(OutlierMm, 3 * median) || active.Count <= MinMarkers)
            {
                return fit;
            }

            outliers.Add(worst.Id);
            active.Remove(worst.Id);
        }
    }

    /// <summary>Per marker: 1 / (1/n_ref + 1/n_new), n = photos it was seen in (at least 1).</summary>
    private static Dictionary<int, double> Weights(WallGeometryDocument reference, WallGeometryDocument solved, List<int> ids)
    {
        var nRef = Observations(reference);
        var nNew = Observations(solved);
        return ids.ToDictionary(id => id, id => 1 / ((1.0 / nRef.GetValueOrDefault(id, 1)) + (1.0 / nNew.GetValueOrDefault(id, 1))));
    }

    private static RigidTransform3D? FitWeighted(
        List<int> ids, IReadOnlyDictionary<int, double[][]> reference, IReadOnlyDictionary<int, double[][]> solved, Dictionary<int, double> weights) =>
        RigidTransform3D.Fit(ids.SelectMany(id => solved[id].Zip(reference[id], (f, t) => (f, t, weights[id]))).ToList());

    private static double Residual(RigidTransform3D fit, double[][] from, double[][] to) =>
        Math.Sqrt(from.Zip(to, (f, t) => Math.Pow(Vec3.Distance(fit.Apply(f), t), 2)).Average());

    /// <summary>The second-largest standard deviation of the points (mm): small when they lie along a line.</summary>
    private static double Spread(List<double[]> points)
    {
        var mean = Vec3.Mean(points);
        var cov = new double[3, 3];
        foreach (var p in points)
        {
            var d = Vec3.Sub(p, mean);
            for (var i = 0; i < 3; i++)
            {
                for (var j = 0; j < 3; j++)
                {
                    cov[i, j] += d[i] * d[j] / points.Count;
                }
            }
        }

        var values = SymmetricEigen.Decompose(cov).Values.OrderDescending().ToList();
        return Math.Sqrt(Math.Max(0, values[1]));
    }

    private static string TooFewMessage(List<int> ids) =>
        ids.Count == 0
            ? "No marker kept its place between the active model and this capture, so the new model cannot be tied to it. "
              + "Keep at least 3 unchanged markers, spread out, visible in the photos when changing markers."
            : $"Only {ids.Count} marker(s) kept their place ({string.Join(", ", ids)}) — keep at least {MinMarkers} unchanged, "
              + "spread out, visible in the photos when changing markers.";

    private static string OutlierNote(List<int> outliers) =>
        outliers.Count == 0 ? string.Empty : $" Markers {string.Join(", ", outliers)} did not fit the others and were left out.";

    private static FrameRegistrationResult Refuse(string message, List<int> used, List<int> changed, List<int> outliers) =>
        new(false, null, used, changed, outliers, null, null, message);
}

/// <summary>The outcome of <see cref="WallFrameRegistration.Register"/>.</summary>
/// <param name="Accepted">True when the new model can be expressed in the reference frame.</param>
/// <param name="Transform">New frame → reference frame, when accepted.</param>
/// <param name="UsedIds">The markers the fit rests on.</param>
/// <param name="ChangedIds">Markers both models have but that changed between the revisions (never used).</param>
/// <param name="OutlierIds">Unchanged markers that disagreed with the rest and were dropped.</param>
/// <param name="RmsMm">Observation-weighted RMS corner residual of the used markers after the fit.</param>
/// <param name="MaxMm">The worst marker's RMS corner residual.</param>
/// <param name="Message">Why it was refused (admin-safe), when refused.</param>
/// <param name="ResidualsMm">Each kept marker's RMS corner residual after the fit, when a fit was made.</param>
public sealed record FrameRegistrationResult(
    bool Accepted,
    RigidTransform3D? Transform,
    IReadOnlyList<int> UsedIds,
    IReadOnlyList<int> ChangedIds,
    IReadOnlyList<int> OutlierIds,
    double? RmsMm,
    double? MaxMm,
    string? Message,
    IReadOnlyDictionary<int, double>? ResidualsMm = null);
