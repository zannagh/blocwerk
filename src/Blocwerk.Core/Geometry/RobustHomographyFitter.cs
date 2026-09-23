namespace Blocwerk.Core.Geometry;

/// <summary>Result of a robust fit: the homography plus which correspondences it explains.</summary>
internal sealed record RobustHomographyFit(PlaneHomography Homography, bool[] Inliers);

/// <summary>
/// MSAC over marker-corner correspondences (plane mm → image px). Hypotheses are every
/// marker's own exact 4-corner fit plus seeded random 4-corner samples across markers; the
/// winner is refit by least squares on its inliers (twice, re-classifying in between).
/// Deterministic: same input, same output.
/// </summary>
internal static class RobustHomographyFitter
{
    private const int RandomSamples = 300;

    /// <param name="pairs">Correspondences; plane mm as source, image px as destination.</param>
    /// <param name="groupSize">Correspondences per marker (4), used to seed per-marker hypotheses.</param>
    /// <param name="thresholdPx">Image-space inlier threshold.</param>
    public static RobustHomographyFit? Fit(IReadOnlyList<PointCorrespondence> pairs, int groupSize, double thresholdPx)
    {
        if (pairs.Count < 4)
        {
            return null;
        }

        var thrSq = thresholdPx * thresholdPx;
        PlaneHomography? best = null;
        var bestCost = double.MaxValue;
        foreach (var hypothesis in Hypotheses(pairs, groupSize))
        {
            var cost = pairs.Sum(p => Math.Min(ErrorSq(hypothesis, p), thrSq));
            if (cost < bestCost)
            {
                bestCost = cost;
                best = hypothesis;
            }
        }

        if (best is null)
        {
            return null;
        }

        for (var pass = 0; pass < 2; pass++)
        {
            var inliers = pairs.Where(p => ErrorSq(best, p) < thrSq).ToList();
            best = PlaneHomography.Fit(inliers) ?? best;
        }

        var mask = pairs.Select(p => ErrorSq(best, p) < thrSq).ToArray();
        return new RobustHomographyFit(best, mask);
    }

    /// <summary>Squared image-space transfer error of one correspondence.</summary>
    public static double ErrorSq(PlaneHomography h, PointCorrespondence p)
    {
        var (x, y) = h.Apply(p.SrcX, p.SrcY);
        if (double.IsNaN(x))
        {
            return double.MaxValue;
        }

        return ((x - p.DstX) * (x - p.DstX)) + ((y - p.DstY) * (y - p.DstY));
    }

    private static IEnumerable<PlaneHomography> Hypotheses(IReadOnlyList<PointCorrespondence> pairs, int groupSize)
    {
        for (var start = 0; start + groupSize <= pairs.Count; start += groupSize)
        {
            var fit = PlaneHomography.Fit(pairs.Skip(start).Take(groupSize).ToList());
            if (fit is not null)
            {
                yield return fit;
            }
        }

        var rng = new Random(12345);
        var sample = new PointCorrespondence[4];
        for (var i = 0; i < RandomSamples; i++)
        {
            var picks = Enumerable.Range(0, pairs.Count).OrderBy(_ => rng.Next()).Take(4).ToArray();
            for (var k = 0; k < 4; k++)
            {
                sample[k] = pairs[picks[k]];
            }

            var fit = PlaneHomography.Fit(sample);
            if (fit is not null)
            {
                yield return fit;
            }
        }
    }
}
