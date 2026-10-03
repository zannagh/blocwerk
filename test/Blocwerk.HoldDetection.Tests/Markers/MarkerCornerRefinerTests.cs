using Blocwerk.Core.Abstractions;
using Blocwerk.HoldDetection.Markers;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Tests.Markers;

public class MarkerCornerRefinerTests
{
    private static readonly string GlyphDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Glyphs");

    /// <summary>A keystoned, rotated view of the 300 px canvas (marker ~135 px in the photo).</summary>
    private static readonly Point2f[] ObliqueView = [new(60, 30), new(340, 60), new(320, 340), new(30, 310)];

    public static TheoryData<string> ParityImages => new() { "img2771-seam-small.jpg", "img2772-kickboard-seam.jpg", "img2783-topleft.jpg" };

    /// <summary>
    /// refine-parity.json holds, per crop, raw ArUco corners and what tools/glyph/geometry/refine.py
    /// makes of them on the same crop. In the reference's sub-pixel mode the port must land within
    /// 0.5 px of it (measured: 0.0004 px).
    /// </summary>
    [Theory]
    [MemberData(nameof(ParityImages))]
    public void RealCrop_MatchesThePythonReference(string file)
    {
        using var gray = Cv2.ImRead(Path.Combine(GlyphDir, file), ImreadModes.Grayscale);
        var expected = RefineParityMarker.LoadAll(Path.Combine(GlyphDir, "refine-parity.json"))[file];
        Assert.NotEmpty(expected);

        foreach (var marker in expected)
        {
            var result = MarkerCornerRefiner.Refine(gray, marker.Raw, method: EdgeSubPixelMethod.Parabola);

            for (var i = 0; i < 4; i++)
            {
                Assert.Equal(marker.Ok[i], result.Status[i] == CornerRefinementStatus.Refined);
                Assert.True(
                    Distance(result.Corners[i], marker.Refined[i]) < 0.5,
                    $"id {marker.Id} corner {i}: C# {result.Corners[i]} vs Python {marker.Refined[i]}");
            }

            Assert.NotNull(result.ResidualPx);
        }
    }

    [Fact]
    public void Synthetic_ScrewHeadsAndPaperEdge_RefinedCornersBeatArucoAndHitTheTruth()
    {
        using var scene = ScrewedMarkerScene.Create(ObliqueView, new Size(400, 380));

        var raw = DetectSingle(scene.Image);
        var result = MarkerCornerRefiner.Refine(scene.Image, raw);

        Assert.All(result.Status, s => Assert.Equal(CornerRefinementStatus.Refined, s));
        var rawErr = Errors(raw, scene.TruthCorners);
        var refinedErr = Errors(result.Corners, scene.TruthCorners);
        Assert.All(refinedErr, e => Assert.True(e < 0.5, $"refined error {e:F3} px")); // measured <= 0.03
        Assert.True(rawErr.Max() > 1.0, $"raw ArUco max error only {rawErr.Max():F3} px: the scene no longer biases it");
        Assert.True(refinedErr.Average() < rawErr.Average() / 3, $"raw {rawErr.Average():F3} vs refined {refinedErr.Average():F3}");
        Assert.InRange(result.ResidualPx!.Value, 0.0, 0.3);
    }

    /// <summary>
    /// A head-on view puts every side within a pixel of axis-aligned. The reference's parabola then
    /// snaps edge samples to pixel cells and shifts the corners by ~0.5 px; the default centroid does not.
    /// </summary>
    [Fact]
    public void AxisAlignedSides_CentroidAvoidsThePixelStaircase()
    {
        Point2f[] headOn = [new(20, 20), new(370, 20), new(370, 350), new(20, 350)];
        using var scene = ScrewedMarkerScene.Create(headOn, new Size(400, 380), 2.0);
        var raw = DetectSingle(scene.Image);

        var centroid = Errors(MarkerCornerRefiner.Refine(scene.Image, raw).Corners, scene.TruthCorners);
        var parabola = Errors(MarkerCornerRefiner.Refine(scene.Image, raw, method: EdgeSubPixelMethod.Parabola).Corners, scene.TruthCorners);

        Assert.All(centroid, e => Assert.True(e < 0.15, $"centroid error {e:F3} px"));
        Assert.True(parabola.Average() > 0.4, $"parabola mean error only {parabola.Average():F3} px");
    }

    [Fact]
    public void SyntheticCorner_IsKeptVerbatimAndFlagged()
    {
        using var scene = ScrewedMarkerScene.Create(ObliqueView, new Size(400, 380));
        var raw = DetectSingle(scene.Image).ToList();
        raw[2] = new MarkerPoint(raw[2].X + 6, raw[2].Y - 4); // reconstructed guess, deliberately off

        var result = MarkerCornerRefiner.Refine(scene.Image, raw, [false, false, true, false]);

        Assert.Equal(CornerRefinementStatus.KeptSynthetic, result.Status[2]);
        Assert.Equal(raw[2], result.Corners[2]);
        Assert.Equal(0.0, result.ShiftPx[2]);
    }

    [Fact]
    public void MarkerCutByTheFrame_KeepsTheOffImageCorner()
    {
        // The canvas is shifted so the marker's TL corner falls left of the photo.
        Point2f[] view = [new(-115, 20), new(185, 10), new(195, 310), new(-120, 320)];
        using var scene = ScrewedMarkerScene.Create(view, new Size(260, 340), 1.0);
        var truth = scene.TruthCorners.Select(p => new MarkerPoint(p.X + 1.5, p.Y + 1.5)).ToList();
        Assert.True(truth[0].X < 0);

        var result = MarkerCornerRefiner.Refine(scene.Image, truth);

        Assert.Equal(CornerRefinementStatus.KeptOutsideImage, result.Status[0]);
        Assert.Equal(truth[0], result.Corners[0]);
        Assert.Equal(CornerRefinementStatus.Refined, result.Status[2]);
        Assert.True(Distance(result.Corners[2], new MarkerPoint(scene.TruthCorners[2].X, scene.TruthCorners[2].Y)) < 0.5);
    }

    [Fact]
    public void TooSmallMarker_KeepsAllCorners()
    {
        using var gray = new Mat(new Size(100, 100), MatType.CV_8UC1, Scalar.All(200));
        MarkerPoint[] corners = [new(40, 40), new(55, 40), new(55, 55), new(40, 55)];

        var result = MarkerCornerRefiner.Refine(gray, corners);

        Assert.All(result.Status, s => Assert.Equal(CornerRefinementStatus.KeptMarkerTooSmall, s));
        Assert.Equal(corners, result.Corners);
        Assert.Null(result.ResidualPx);
    }

    [Fact]
    public void NoEdges_FitFailsAndKeepsTheOriginal()
    {
        using var gray = new Mat(new Size(200, 200), MatType.CV_8UC1, Scalar.All(200));
        MarkerPoint[] corners = [new(50, 50), new(150, 50), new(150, 150), new(50, 150)];

        var result = MarkerCornerRefiner.Refine(gray, corners);

        Assert.All(result.Status, s => Assert.Equal(CornerRefinementStatus.KeptEdgeFitFailed, s));
        Assert.Equal(corners, result.Corners);
        Assert.Equal(0, result.RefinedCount);
    }

    [Fact]
    public void RefineAll_LeavesSyntheticMarkersAloneAndUpdatesDerivedFields()
    {
        using var scene = ScrewedMarkerScene.Create(ObliqueView, new Size(400, 380));
        var raw = DetectSingle(scene.Image);
        var observed = Marker(raw, synthetic: false);
        var synthetic = Marker(raw, synthetic: true);

        var refined = MarkerCornerRefiner.RefineAll(scene.Image, [observed, synthetic]);

        Assert.Same(synthetic, refined[1]);
        Assert.NotEqual(observed.CornersPx, refined[0].CornersPx);
        Assert.Equal(refined[0].CornersPx[1].X / 400.0, refined[0].CornersNormalized[1].X, 9);
        Assert.NotEqual(observed.SidePx, refined[0].SidePx);
    }

    private static DetectedMarker Marker(IReadOnlyList<MarkerPoint> corners, bool synthetic) => new()
    {
        Id = 7,
        CornersPx = corners,
        CornersNormalized = corners,
        SidePx = 1,
        EdgeRatio = 1,
        Synthetic = synthetic,
    };

    /// <summary>ArUco's own corners (nested copies collapsed, not refined) of the scene's one marker.</summary>
    private static IReadOnlyList<MarkerPoint> DetectSingle(Mat gray)
    {
        var result = ArucoMarkerDetectionService.Detect(gray, MarkerDetectionOptions.Default with { RefineCorners = false });
        Assert.Equal(new[] { 7 }, result.Markers.Select(m => m.Id));
        return result.Markers[0].CornersPx;
    }

    private static double[] Errors(IReadOnlyList<MarkerPoint> corners, Point2d[] truth) =>
        corners.Select((c, i) => Distance(c, new MarkerPoint(truth[i].X, truth[i].Y))).ToArray();

    private static double Distance(MarkerPoint a, MarkerPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
