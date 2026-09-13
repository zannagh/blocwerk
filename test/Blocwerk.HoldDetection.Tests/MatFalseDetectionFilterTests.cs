using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Detection;

namespace Blocwerk.HoldDetection.Tests;

/// <summary>
/// Unit tests for <see cref="MatFalseDetectionFilter"/>. These are fully self-contained:
/// a synthetic detection population approximating the real Attic distribution (median
/// radius ~0.01, most holds mid-wall) plus the confirmed crash-mat false detections
/// (Y ~= 0.82, radius ~= 0.065). No running app, model, or image is required — the filter
/// is a pure function over the normalised center + radius of each detection.
/// </summary>
public class MatFalseDetectionFilterTests
{
    private const string NormalColour = "blue";

    // The three human-confirmed mats plus a 4th same-signature detection.
    private static readonly DetectedHold Mat1 = new(X: 0.20, Y: 0.813, Radius: 0.0637, Color: "grey", Confidence: 0.30);
    private static readonly DetectedHold Mat2 = new(X: 0.45, Y: 0.817, Radius: 0.0662, Color: "grey", Confidence: 0.28);
    private static readonly DetectedHold Mat3 = new(X: 0.70, Y: 0.821, Radius: 0.0680, Color: "grey", Confidence: 0.31);
    private static readonly DetectedHold Mat4 = new(X: 0.55, Y: 0.813, Radius: 0.0637, Color: "grey", Confidence: 0.27);

    // Only a size anomaly: a legitimately large volume placed mid-wall. Must be KEPT.
    private static readonly DetectedHold LargeMidWallVolume = new(X: 0.5, Y: 0.40, Radius: 0.030, Color: "white", Confidence: 0.9);

    // Only a position anomaly: a normal-sized low hold that sits within a wall that runs
    // continuously down to it (NO floor gap). Must be KEPT by both rules.
    private static readonly DetectedHold LowNormalHold = new(X: 0.5, Y: 0.80, Radius: 0.010, Color: NormalColour, Confidence: 0.8);

    [Fact]
    public void AllFourMats_AreDropped()
    {
        var population = BuildPopulation();
        population.AddRange([Mat1, Mat2, Mat3, Mat4]);

        var result = MatFalseDetectionFilter.Classify(population);

        Assert.Contains(Mat1, result.Dropped);
        Assert.Contains(Mat2, result.Dropped);
        Assert.Contains(Mat3, result.Dropped);
        Assert.Contains(Mat4, result.Dropped);
        Assert.DoesNotContain(Mat1, result.Kept);
        Assert.DoesNotContain(Mat4, result.Kept);
    }

    [Fact]
    public void LargeVolumeMidWall_OnlySizeAnomaly_IsKept()
    {
        var population = BuildPopulation();
        population.AddRange([Mat1, Mat2, Mat3, LargeMidWallVolume]);

        var result = MatFalseDetectionFilter.Classify(population);

        Assert.Contains(LargeMidWallVolume, result.Kept);
        Assert.DoesNotContain(LargeMidWallVolume, result.Dropped);
    }

    [Fact]
    public void LowHoldWithNormalRadius_NoFloorGap_IsKept()
    {
        // A wall whose holds run continuously down past the low hold: no floor gap, so
        // neither the radius+position rule nor the floor cutoff should touch it.
        var population = BuildContinuousPopulation(maxY: 0.82);
        population.Add(LowNormalHold);

        var result = MatFalseDetectionFilter.Classify(population);

        Assert.Contains(LowNormalHold, result.Kept);
        Assert.DoesNotContain(LowNormalHold, result.Dropped);
    }

    [Fact]
    public void SmallFloorCluster_BelowClearGap_IsDropped_EvenThoughSmallRadius()
    {
        // ~300 normal band holds (Y 0.1-0.75) plus a SEPARATE cluster of ~10 small-radius
        // holds at Y~=0.9 with a clear gap (nothing between 0.78 and 0.9). The floor cluster
        // is NOT a radius outlier, so only the gap-based cutoff can catch it.
        var population = BuildPopulation();
        var floorCluster = BuildSmallFloorCluster();
        population.AddRange(floorCluster);

        var result = MatFalseDetectionFilter.Classify(population);

        Assert.All(floorCluster, h => Assert.Contains(h, result.Dropped));
        Assert.All(floorCluster, h => Assert.DoesNotContain(h, result.Kept));

        // Every genuine band hold survives.
        Assert.Equal(300, result.Kept.Count);
        Assert.All(result.Kept, h => Assert.True(h.Y <= 0.75));
    }

    [Fact]
    public void HoldsContinuousToBottom_NoGap_FloorCutoffDropsNothingExtra()
    {
        // Holds run continuously down to the very bottom with no separation. The floor
        // cutoff must NOT fire; with no radius outliers either, nothing is dropped.
        var population = BuildContinuousPopulation(maxY: 0.98);

        var result = MatFalseDetectionFilter.Classify(population);

        Assert.Empty(result.Dropped);
        Assert.Equal(population.Count, result.Kept.Count);
    }

    [Fact]
    public void NormalHolds_AreAllKept()
    {
        var population = BuildPopulation();

        var result = MatFalseDetectionFilter.Classify(population);

        Assert.Empty(result.Dropped);
        Assert.Equal(population.Count, result.Kept.Count);
    }

    [Fact]
    public void MixedPopulation_KeepsEveryLegitHold_AndDropsOnlyMats()
    {
        // Mats sit in the separated bottom tail, so BOTH rules flag them (their union is
        // still exactly the four mats). The mid-wall volume is a radius-only anomaly and
        // sits well above the floor gap, so neither rule touches it.
        var population = BuildPopulation();
        population.AddRange([LargeMidWallVolume, Mat1, Mat2, Mat3, Mat4]);

        var result = MatFalseDetectionFilter.Classify(population);

        Assert.Equal(4, result.Dropped.Count);
        Assert.All(result.Dropped, h => Assert.True(h.Radius > 0.05 && h.Y > 0.8));
        Assert.Contains(LargeMidWallVolume, result.Kept);
        Assert.Equal(301, result.Kept.Count);
    }

    [Fact]
    public void TinyPopulation_IsLeftUntouched()
    {
        var few = new List<DetectedHold> { Mat1, Mat2, Mat3 };

        var result = MatFalseDetectionFilter.Classify(few);

        Assert.Empty(result.Dropped);
        Assert.Equal(3, result.Kept.Count);
    }

    /// <summary>
    /// A deterministic synthetic population of ~300 legitimate holds approximating the real
    /// distribution: radii clustered around a ~0.01 median with a realistic tail up to the
    /// low 0.02s (p90 ~= 0.017), and Y spread across the wall with most holds between 0.1
    /// and 0.75. Contains no mats. Sized so the handful of mats added by the tests land in
    /// the ~1% bottom tail (beyond p97), mirroring the real ~347-detection wall.
    /// </summary>
    private static List<DetectedHold> BuildPopulation()
    {
        var holds = new List<DetectedHold>();
        var rng = new Random(1234);

        for (int i = 0; i < 300; i++)
        {
            // Radius: mostly 0.006-0.014, with a small realistic tail toward ~0.022.
            double radius = 0.006 + (rng.NextDouble() * 0.008);
            if (i % 12 == 0)
            {
                radius = 0.015 + (rng.NextDouble() * 0.007);
            }

            // Y: clustered across the climbable wall, none in the bottom mat margin.
            double y = 0.10 + (rng.NextDouble() * 0.65);
            double x = rng.NextDouble();

            holds.Add(new DetectedHold(
                X: Math.Round(x, 4),
                Y: Math.Round(y, 4),
                Radius: Math.Round(radius, 4),
                Color: NormalColour,
                Confidence: 0.7));
        }

        return holds;
    }

    /// <summary>
    /// A deterministic population of 300 legitimate holds whose Y runs CONTINUOUSLY from
    /// 0.10 down to <paramref name="maxY"/> with no vertical separation — the "wall has low
    /// holds but no floor gap" case. Radii mirror <see cref="BuildPopulation"/> so nothing
    /// is a radius outlier either.
    /// </summary>
    private static List<DetectedHold> BuildContinuousPopulation(double maxY)
    {
        var holds = new List<DetectedHold>();
        var rng = new Random(4321);
        double span = maxY - 0.10;

        for (int i = 0; i < 300; i++)
        {
            double radius = 0.006 + (rng.NextDouble() * 0.008);
            if (i % 12 == 0)
            {
                radius = 0.015 + (rng.NextDouble() * 0.007);
            }

            double y = 0.10 + (rng.NextDouble() * span);
            double x = rng.NextDouble();

            holds.Add(new DetectedHold(
                X: Math.Round(x, 4),
                Y: Math.Round(y, 4),
                Radius: Math.Round(radius, 4),
                Color: NormalColour,
                Confidence: 0.7));
        }

        return holds;
    }

    /// <summary>
    /// A dense tangle of ~10 SMALL-radius detections clustered at Y ~= 0.9 — the crash-mat
    /// floor false-detections. Radius is around the population median (NOT an outlier), so
    /// only the position-based floor cutoff can catch them. There is nothing between the
    /// wall band (ends at 0.75) and this cluster, giving a clear ~0.13 gap.
    /// </summary>
    private static List<DetectedHold> BuildSmallFloorCluster()
    {
        var holds = new List<DetectedHold>();
        var rng = new Random(9876);

        for (int i = 0; i < 10; i++)
        {
            double y = 0.88 + (rng.NextDouble() * 0.04);
            double radius = 0.008 + (rng.NextDouble() * 0.004);
            double x = rng.NextDouble();

            holds.Add(new DetectedHold(
                X: Math.Round(x, 4),
                Y: Math.Round(y, 4),
                Radius: Math.Round(radius, 4),
                Color: "grey",
                Confidence: 0.22));
        }

        return holds;
    }
}
