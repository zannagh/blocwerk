using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Detection;

/// <summary>
/// Rejects detections that are almost certainly crash mats or the floor below the wall
/// rather than climbing holds. YOLO/OpenCV occasionally fire on the padded mats at the
/// base of a wall; on the one wall we have been able to tune against (The Attic), those
/// false detections share a strongly separable signature: an over-sized radius (~4-7x the
/// median hold) that sits in the very bottom margin of the detected-hold cloud.
///
/// The classifier is deliberately <b>population-relative</b> rather than hard-coded to
/// absolute pixel/normalised values: we only have a single wall to calibrate on, so any
/// absolute threshold would over-fit. Instead we measure the radius and vertical
/// distribution of THIS image's own detections and flag only the joint outliers.
///
/// A detection is rejected ONLY when BOTH anomaly signals agree (logical AND):
/// <list type="bullet">
/// <item><b>Radius anomaly</b> — the radius is a robust outlier above the population,
/// using the Iglewicz-Hoaglin modified z-score (median/MAD based, resistant to the very
/// outliers we are hunting). A modified z-score above <see cref="RadiusModifiedZThreshold"/>
/// (the textbook 3.5 cut-off) marks a potential outlier.</item>
/// <item><b>Position anomaly</b> — the normalised Y sits below where real holds cluster,
/// i.e. beyond <see cref="PositionPercentile"/> of the detected-hold Y distribution
/// (top = 0, bottom = 1). Mats live in the bottom margin.</item>
/// </list>
///
/// Requiring BOTH is what keeps the rule safe: a legitimate large volume placed mid-wall
/// trips only the radius signal and is kept; a normal-radius hold low on the wall trips
/// only the position signal and is kept. Colour (mats read as low-saturation grey) is a
/// plausible third corroborating signal but is intentionally NOT consulted here — the
/// detection stage does not carry a sampled colour for every blob, and the design says
/// colour must never be REQUIRED because some mats are coloured.
///
/// A SECOND, position-only rule catches a failure mode the radius+position rule misses:
/// the dense tangle of SMALL circles the detector fires on the crash-mat/floor. Those are
/// NOT radius outliers (they are small), so the AND rule above never touches them. Real
/// wall holds cluster in a vertical band; the floor detections sit as a SEPARATE cluster
/// below a clear vertical GAP. We therefore look at the Y distribution's lower region and,
/// only if there is an unmistakable separation (largest neighbour gap ≫ the typical
/// neighbour spacing there, AND the gap sits low on the wall), treat everything below the
/// gap as floor and drop it regardless of radius. When holds run continuously down to the
/// bottom (no gap), we do NOT cut — this keeps the rule conservative for walls that
/// genuinely have low kickboard holds. The final result is the UNION of the radius+position
/// drops and the floor-cutoff drops.
///
/// CAVEAT: tuned against one wall. Both rules are population-relative and conservative, but
/// (a) a genuine over-sized volume bolted into the very bottom margin would match the
/// radius+position rule, and (b) a genuinely low kickboard hold that happens to sit alone
/// below a clear gap would be clipped by the floor cutoff. The user explicitly accepted
/// that small clipping risk in exchange for removing the floor tangle; the gap-guard (only
/// cut on an unmistakable, low separation) is what bounds it. A reviewer changing wall
/// layouts should re-check.
/// </summary>
public static class MatFalseDetectionFilter
{
    /// <summary>
    /// Iglewicz-Hoaglin modified z-score above which a radius counts as an outlier.
    /// 3.5 is the standard "potential outlier" cut-off; the confirmed mats score ~12,
    /// while the population p90 radius scores ~2.2 and is left untouched.
    /// </summary>
    public const double RadiusModifiedZThreshold = 3.5;

    /// <summary>
    /// Percentile of the detected-hold Y distribution above which a detection is
    /// considered to be in the bottom margin (below where real holds cluster).
    /// </summary>
    public const double PositionPercentile = 0.97;

    /// <summary>
    /// Minimum number of detections required before the filter runs at all. Below this
    /// the population statistics (median/MAD/percentile) are too unstable to trust, so
    /// we conservatively keep everything.
    /// </summary>
    public const int MinimumPopulationSize = 20;

    /// <summary>
    /// Percentile of the Y distribution above which we look for a floor gap. We only
    /// consider a separation in the lower region of the wall (real holds above this are
    /// never cut) so the floor cutoff can never bite into the main body of the wall.
    /// </summary>
    public const double FloorRegionPercentile = 0.85;

    /// <summary>
    /// How many times larger than the median neighbour gap (in the lower region) the
    /// largest neighbour gap must be before we treat it as a genuine floor separation.
    /// A uniform, gap-free band already produces a largest-to-median neighbour-gap ratio
    /// of roughly 5-6 from randomness alone, so this must sit comfortably above that to
    /// avoid cutting a continuous wall; confirmed floor tangles separate from the wall by
    /// an order of magnitude or more (ratios well past 50), so 8 leaves wide headroom on
    /// both sides. Tuned against one wall (The Attic); re-check if wall layouts change.
    /// </summary>
    public const double FloorGapRatio = 8.0;

    /// <summary>
    /// The floor gap must sit below this percentile of the Y distribution to count. This
    /// stops a merely sparse patch high on the wall from being mistaken for a floor
    /// separation: a real floor gap sits in the bottom margin, below where holds cluster.
    /// </summary>
    public const double FloorCutMinimumPercentile = 0.90;

    /// <summary>
    /// Minimum number of detections in the lower region before the floor cutoff runs. With
    /// fewer points the neighbour-gap statistics are too unstable to distinguish a genuine
    /// separation from noise, so we skip the floor cutoff and fall back to radius+position.
    /// </summary>
    public const int MinimumFloorRegionSize = 8;

    /// <summary>
    /// The 0.6745 constant that scales the median absolute deviation to be a consistent
    /// estimator of the standard deviation for normally distributed data.
    /// </summary>
    private const double MadToSigma = 0.6745;

    /// <summary>
    /// Classifies a detection set, partitioning it into kept holds and likely mats.
    /// Pure and deterministic: no I/O, no mutation of the input.
    /// </summary>
    public static MatFilterResult Classify(IReadOnlyList<DetectedHold> detections)
    {
        if (detections is null || detections.Count < MinimumPopulationSize)
        {
            return new MatFilterResult(detections ?? Array.Empty<DetectedHold>(), Array.Empty<DetectedHold>());
        }

        double radiusMedian = Median(detections.Select(d => d.Radius));
        double radiusMad = Median(detections.Select(d => Math.Abs(d.Radius - radiusMedian)));
        double yThreshold = Percentile(detections.Select(d => d.Y), PositionPercentile);

        // A degenerate radius spread (near-identical radii) makes the modified z-score
        // undefined/explosive; without a meaningful spread we cannot call anything an
        // outlier, so we keep everything.
        if (radiusMad <= 0)
        {
            return new MatFilterResult(detections, Array.Empty<DetectedHold>());
        }

        // Position-only floor separation, computed once for the whole population. Null when
        // there is no clear gap (holds continuous to the bottom) — the conservative default.
        double? floorCutY = ComputeFloorCutY(detections);

        var kept = new List<DetectedHold>(detections.Count);
        var dropped = new List<DetectedHold>();

        foreach (var hold in detections)
        {
            bool isRadiusPositionMat = IsLikelyMat(hold, radiusMedian, radiusMad, yThreshold);
            bool isBelowFloorGap = floorCutY.HasValue && hold.Y > floorCutY.Value;

            // Union of both rules; a hold is dropped if EITHER fires, so the two lists stay
            // a clean partition of the input with no duplicates.
            if (isRadiusPositionMat || isBelowFloorGap)
            {
                dropped.Add(hold);
            }
            else
            {
                kept.Add(hold);
            }
        }

        return new MatFilterResult(kept, dropped);
    }

    /// <summary>
    /// Finds the Y coordinate of a clear floor separation, or null when there is none.
    /// Everything with Y strictly greater than the returned value is floor. See the type
    /// summary for the rationale and the accepted low-hold clipping risk.
    /// </summary>
    private static double? ComputeFloorCutY(IReadOnlyList<DetectedHold> detections)
    {
        double regionThreshold = Percentile(detections.Select(d => d.Y), FloorRegionPercentile);
        double[] lowerYs = detections
            .Select(d => d.Y)
            .Where(y => y >= regionThreshold)
            .OrderBy(y => y)
            .ToArray();

        if (lowerYs.Length < MinimumFloorRegionSize)
        {
            return null;
        }

        double[] gaps = new double[lowerYs.Length - 1];
        double maxGap = 0;
        int maxGapIndex = 0;
        for (int i = 0; i < gaps.Length; i++)
        {
            gaps[i] = lowerYs[i + 1] - lowerYs[i];
            if (gaps[i] > maxGap)
            {
                maxGap = gaps[i];
                maxGapIndex = i;
            }
        }

        double medianGap = Median(gaps);

        // No spread (or no separation clearly larger than the typical spacing): do not cut.
        if (medianGap <= 0 || maxGap <= FloorGapRatio * medianGap)
        {
            return null;
        }

        double gapMidpoint = (lowerYs[maxGapIndex] + lowerYs[maxGapIndex + 1]) / 2.0;
        double lowEnoughThreshold = Percentile(detections.Select(d => d.Y), FloorCutMinimumPercentile);

        // The gap must sit low on the wall, not be a sparse patch high up.
        if (gapMidpoint < lowEnoughThreshold)
        {
            return null;
        }

        return gapMidpoint;
    }

    /// <summary>
    /// Convenience wrapper returning only the surviving holds.
    /// </summary>
    public static List<DetectedHold> RemoveLikelyMats(IReadOnlyList<DetectedHold> detections)
    {
        return Classify(detections).Kept.ToList();
    }

    private static bool IsLikelyMat(DetectedHold hold, double radiusMedian, double radiusMad, double yThreshold)
    {
        double modifiedZ = MadToSigma * (hold.Radius - radiusMedian) / radiusMad;
        bool radiusAnomaly = modifiedZ > RadiusModifiedZThreshold;
        bool positionAnomaly = hold.Y > yThreshold;
        return radiusAnomaly && positionAnomaly;
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0)
        {
            return 0;
        }

        int mid = sorted.Length / 2;
        if (sorted.Length % 2 == 1)
        {
            return sorted[mid];
        }

        return (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        double[] sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0)
        {
            return 0;
        }

        // Nearest-rank: smallest value whose rank covers the requested fraction.
        int rank = (int)Math.Ceiling(percentile * sorted.Length);
        int index = Math.Clamp(rank - 1, 0, sorted.Length - 1);
        return sorted[index];
    }
}
