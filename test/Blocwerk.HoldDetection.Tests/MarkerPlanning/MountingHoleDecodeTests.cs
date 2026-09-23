// <copyright file="MountingHoleDecodeTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.MarkerPlanning;
using Xunit.Abstractions;

namespace Blocwerk.HoldDetection.Tests.MarkerPlanning;

/// <summary>
/// Mounting holes against the app's detector. The marker pages are rendered with the PDF's own drawing
/// code, dressed as a photo of a marker screwed to a wall — dark screw heads (the thing that biased
/// corners 3–29 px on the wall) and a textured wall right outside the cut line — and read back, refined
/// and raw. Every corner is compared with where the PDF drew it, against a plain print on the same wall.
/// The tight default (1 mm to the head, 1 mm to the cut edge) is as good as a plain print once its white
/// border spans ~3 photo px; below that it is measured and pinned as worse — the reason for the planner's
/// "mounting-holes-tight" warning.
/// </summary>
public class MountingHoleDecodeTests(ITestOutputHelper output)
{
    /// <summary>60 dpi: a 50 mm marker ≈ 118 px, a 125 mm one ≈ 295 px — close-up print scale.</summary>
    private const float PrintDpi = 60;

    /// <summary>Lens/demosaic softness of a real photo at marker-planning scale.</summary>
    private const double PhotoBlurPx = 0.7;

    /// <summary>How much worse than the plain print a corner may get.</summary>
    private const double AllowedExtraErrorPx = 0.5;

    private static readonly MarkerDetectionOptions AllIds = MarkerDetectionOptions.Default with
    {
        AllowedIds = Enumerable.Range(0, ArucoDict4X4.Count).ToHashSet(),
    };

    private static readonly double[] PrintSizes = [50, 50, 50, 50, 125, 125, 125, 125];

    /// <summary>
    /// Print scale, 50 and 125 mm markers, tight default gaps, on white paper and on walls: every marker
    /// decodes and the refined corners are as good as a plain print. (Before the detector kept the black
    /// square over the paper's outline, the walls lost up to 5 of 8 markers or moved corners by 32 px.)
    /// </summary>
    [Theory]
    [InlineData(3, 6, null)]
    [InlineData(3, 6, 60.0)]
    [InlineData(3, 6, 128.0)]
    [InlineData(3.5, 15, null)]
    [InlineData(3.5, 15, 60.0)]
    [InlineData(3.5, 15, 128.0)]
    public void TightDefaults_PrintScale_AreAsGoodAsAPlainPrint(double hole, double head, double? wall)
    {
        var holes = new MountingHoles(true, hole, head);
        var (withHoles, plain) = Compare(PrintSizes, holes, PrintDpi, new PhotoDressing(head, wall, null, 0), refine: true);

        Report($"tight print {hole}/{head} wall {Grey(wall)} refine True", withHoles, plain);
        AssertAsGood(withHoles, plain);
    }

    /// <summary>
    /// Photo scale, tight default gaps, softened, dark heads, textured walls — at 60 and 120 px per marker
    /// (the planner's corner target and a close-up), 125 mm with a 6 mm head, 50 mm with 6 and 15 mm heads.
    /// </summary>
    [Theory]
    [InlineData(125, 3, 6, 60)]
    [InlineData(125, 3, 6, 120)]
    [InlineData(125, 3.5, 15, 60)]
    [InlineData(50, 3, 6, 60)]
    [InlineData(50, 3, 6, 120)]
    [InlineData(50, 3.5, 15, 60)]
    public void TightDefaults_PhotoScale_AreAsGoodAsAPlainPrint(double size, double hole, double head, double markerPx)
    {
        var holes = new MountingHoles(true, hole, head);
        Assert.True(MountingHoleSafety.IsSafe(size, holes, markerPx / size));
        foreach (var wall in new double?[] { null, 60, 128 })
        {
            var (withHoles, plain) = Compare([size, size, size, size], holes, DpiFor(size, markerPx), new PhotoDressing(head, wall, null, PhotoBlurPx), refine: true);
            Report($"tight photo {markerPx} px {size} mm {hole}/{head} wall {Grey(wall)} refine True", withHoles, plain);
            AssertAsGood(withHoles, plain);
        }
    }

    /// <summary>
    /// The one limit left: 125 mm markers far away (30 px), where the default 6.8 mm border is ~1.6 photo px.
    /// They still decode, but the refined corners drift toward the wall — pinned so the warning is revisited
    /// when that changes.
    /// </summary>
    [Theory]
    [InlineData(60.0)]
    [InlineData(128.0)]
    public void TightDefaults_BelowTheBorderLimit_AreWorse(double wall)
    {
        const double Size = 125;
        const double MarkerPx = 30;
        Assert.False(MountingHoleSafety.IsSafe(Size, MountingHoles.Default, MarkerPx / Size));
        var (withHoles, plain) = Compare([Size, Size, Size, Size], MountingHoles.Default, DpiFor(Size, MarkerPx), new PhotoDressing(6, wall, null, PhotoBlurPx), refine: true);

        Report($"tight photo {MarkerPx} px {Size} mm wall {Grey(wall)} refine True", withHoles, plain);
        Assert.DoesNotContain(withHoles.Values, double.IsNaN);
        AssertWorse(withHoles, plain);
    }

    /// <summary>
    /// Photo scale with the gaps the planner recommends for that scale (<see cref="MountingHoleSafety"/>):
    /// as good as a plain print, refined.
    /// </summary>
    [Theory]
    [InlineData(125, 3, 6, 30)]
    [InlineData(125, 3, 6, 40)]
    [InlineData(125, 3, 6, 45)]
    [InlineData(125, 3, 6, 60)]
    [InlineData(125, 3, 6, 120)]
    [InlineData(100, 3, 6, 60)]
    [InlineData(80, 3, 6, 60)]
    [InlineData(100, 3, 6, 40)]
    [InlineData(50, 3, 6, 40)]
    [InlineData(50, 3, 6, 45)]
    [InlineData(50, 3, 6, 60)]
    [InlineData(50, 3, 6, 120)]
    [InlineData(50, 3.5, 15, 60)]
    public void SafeGaps_AreAsGoodAsAPlainPrint(double size, double hole, double head, double markerPx)
    {
        var holes = MountingHoleSafety.SafeGaps(size, new MountingHoles(true, hole, head), markerPx / size);
        Assert.NotNull(holes);
        Assert.True(MountingHoleSafety.IsSafe(size, holes, markerPx / size));
        foreach (var wall in new double?[] { null, 60, 128 })
        {
            foreach (var refine in new[] { true, false })
            {
                var (withHoles, plain) = Compare([size, size, size, size], holes, DpiFor(size, markerPx), new PhotoDressing(head, wall, null, PhotoBlurPx), refine);
                Report($"safe {holes.GapToMarkerMm}/{holes.GapToEdgeMm} mm photo {markerPx} px {size} mm {hole}/{head} wall {Grey(wall)} refine {refine}", withHoles, plain);
                if (refine)
                {
                    AssertAsGood(withHoles, plain);
                }
            }
        }
    }

    /// <summary>Print scale with the recommended gaps, 50 and 125 mm on one page set.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(60.0)]
    [InlineData(128.0)]
    public void SafeGaps_PrintScale_AreAsGoodAsAPlainPrint(double? wall)
    {
        var holes = MountingHoleSafety.SafeGaps(125, MountingHoles.Default)!;
        var (withHoles, plain) = Compare(PrintSizes, holes, PrintDpi, new PhotoDressing(holes.ScrewHeadDiameterMm, wall, null, 0), refine: true);

        Report($"safe {holes.GapToMarkerMm}/{holes.GapToEdgeMm} mm print wall {Grey(wall)} refine True", withHoles, plain);
        AssertAsGood(withHoles, plain);
    }

    private static float DpiFor(double sizeMm, double markerPx) => (float)(markerPx / sizeMm * 25.4);

    private static string Grey(double? wall) => wall?.ToString("0", CultureInfo.InvariantCulture) ?? "white";

    /// <summary>The plan with holes and the same plan without, both dressed alike (the plain one without heads).</summary>
    private static (Dictionary<int, double> WithHoles, Dictionary<int, double> Plain) Compare(
        double[] sizes, MountingHoles holes, float dpi, PhotoDressing dressing, bool refine)
    {
        var options = AllIds with { RefineCorners = refine };
        var withHoles = MarkerPhotoSim.CornerErrors(MarkerPhotoSim.TestPlan(sizes, holes), dpi, dressing, options);
        var plain = MarkerPhotoSim.CornerErrors(MarkerPhotoSim.TestPlan(sizes, null), dpi, dressing with { HeadMm = null }, options);
        return (withHoles, plain);
    }

    private static void AssertAsGood(Dictionary<int, double> withHoles, Dictionary<int, double> plain)
    {
        Assert.DoesNotContain(plain.Values, double.IsNaN);
        var baselineMax = plain.Values.Max();
        Assert.All(withHoles, kv => Assert.True(
            kv.Value <= baselineMax + AllowedExtraErrorPx,
            $"marker {kv.Key}: corner error {kv.Value:0.000} px vs {baselineMax:0.000} px without holes"));
    }

    /// <summary>Pins the measured damage: a corner clearly worse than the plain print.</summary>
    private static void AssertWorse(Dictionary<int, double> withHoles, Dictionary<int, double> plain)
    {
        Assert.DoesNotContain(plain.Values, double.IsNaN);
        var lost = withHoles.Values.Any(double.IsNaN);
        var worst = withHoles.Values.Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Max();
        Assert.True(lost || worst > plain.Values.Max() + AllowedExtraErrorPx, "a sub-3 px border no longer hurts the corners — revisit MountingHoleSafety.MinBorderPx");
    }

    private void Report(string label, Dictionary<int, double> withHoles, Dictionary<int, double> plain)
    {
        var decoded = withHoles.Values.Where(v => !double.IsNaN(v)).ToList();
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{label}: decoded {decoded.Count}/{withHoles.Count}, worst corner {decoded.DefaultIfEmpty(double.NaN).Max():0.00} px; plain decoded {plain.Values.Count(v => !double.IsNaN(v))}/{plain.Count}, worst {plain.Values.Max():0.00} px"));
    }
}
