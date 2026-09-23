using Blocwerk.Core.Abstractions;
using Blocwerk.HoldDetection.Markers;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Tests.Markers;

public class MarkerDetectionTests
{
    private static readonly string GlyphDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Glyphs");

    /// <summary>
    /// Crops of the owner's wall photos (capture 1, 2026-09-22), downscaled. Expected ids come
    /// from tools/glyph/detections.json for the markers fully inside each crop.
    /// </summary>
    [Theory]
    [InlineData("img2772-kickboard-seam.jpg", new[] { 2, 7, 8, 33 })]
    [InlineData("img2771-seam-small.jpg", new[] { 2, 7, 8, 33 })]
    [InlineData("img2783-topleft.jpg", new[] { 0, 5, 12 })]
    public async Task RealCrop_DetectsExactlyTheExpectedIds(string file, int[] expected)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(GlyphDir, file));

        var result = await new ArucoMarkerDetectionService().DetectAsync(bytes, null, CancellationToken.None);

        Assert.Equal(expected, result.Markers.Select(m => m.Id).ToArray());
        Assert.False(result.Suspicious);
        Assert.All(result.Markers, m =>
        {
            Assert.Equal(4, m.CornersPx.Count);
            Assert.All(m.CornersNormalized, c => Assert.InRange(c.X, 0.0, 1.0));
            Assert.All(m.CornersNormalized, c => Assert.InRange(c.Y, 0.0, 1.0));
            Assert.InRange(m.EdgeRatio, 1.0, 4.0);
        });
    }

    [Fact]
    public void OutOfRangeId_IsRejectedWithReason()
    {
        using var scene = Scene([new PlacedMarker(3, 100, 100), new PlacedMarker(40, 500, 100)]);

        var result = ArucoMarkerDetectionService.Detect(scene.Image, MarkerDetectionOptions.Default);

        Assert.Equal([3], result.Markers.Select(m => m.Id));
        var rejected = Assert.Single(result.Rejected);
        Assert.Equal(40, rejected.Id);
        Assert.Equal(MarkerRejectionReason.IdNotAllowed, rejected.Reason);
        Assert.False(result.Suspicious);
    }

    [Fact]
    public void DuplicateId_RejectsTheWorseCandidateAndFlagsTheImage()
    {
        // Same id twice: a big one and a small one. The small one must lose.
        using var scene = Scene([new PlacedMarker(7, 100, 100, 200), new PlacedMarker(7, 600, 150, 90)]);

        var result = ArucoMarkerDetectionService.Detect(scene.Image, MarkerDetectionOptions.Default);

        var kept = Assert.Single(result.Markers);
        Assert.Equal(7, kept.Id);
        var loser = Assert.Single(result.Rejected);
        Assert.Equal(MarkerRejectionReason.DuplicateId, loser.Reason);
        Assert.True(kept.SidePx > loser.SidePx);
        Assert.True(result.Suspicious);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void Validator_RejectsTooSmallAndTooOblique()
    {
        MarkerCandidate[] candidates =
        [
            Quad(1, 0, 0, 100, 100), // fine
            Quad(2, 0, 0, 12, 12), // 12 px: the real id-17 false positive size
            Quad(4, 0, 0, 200, 40), // edge ratio 5
        ];

        var outcome = MarkerCandidateValidator.Validate(candidates, MarkerDetectionOptions.Default, 1000, 1000);

        Assert.Equal([1], outcome.Accepted.Select(m => m.Id));
        Assert.Equal(MarkerRejectionReason.TooSmall, outcome.Rejected.Single(r => r.Id == 2).Reason);
        Assert.Equal(MarkerRejectionReason.TooOblique, outcome.Rejected.Single(r => r.Id == 4).Reason);
    }

    [Fact]
    public void Validator_HonoursCustomAllowList()
    {
        var options = MarkerDetectionOptions.Default with { AllowedIds = new HashSet<int> { 5 } };

        var outcome = MarkerCandidateValidator.Validate([Quad(1, 0, 0, 100, 100), Quad(5, 300, 0, 100, 100)], options, 1000, 1000);

        Assert.Equal([5], outcome.Accepted.Select(m => m.Id));
        Assert.Equal(MarkerRejectionReason.IdNotAllowed, Assert.Single(outcome.Rejected).Reason);
    }

    [Fact]
    public void Validator_CollapsesNestedCopiesOfOneMarker_KeepingTheBlackSquare()
    {
        // The paper outline (thin white border, 125 → 139 mm) and the square found at two threshold windows.
        MarkerCandidate[] candidates =
        [
            Quad(3, 93, 93, 139, 139),
            Quad(3, 100, 100, 125, 125),
            Quad(3, 100.5, 100.5, 124, 124),
            Quad(3, 600, 100, 125, 125), // the same id somewhere else stays a duplicate
        ];

        var outcome = MarkerCandidateValidator.Validate(candidates, MarkerDetectionOptions.Default, 1000, 1000);

        var kept = Assert.Single(outcome.Accepted);
        Assert.Equal(125, kept.SidePx, 6);
        Assert.Equal(100, kept.CornersPx[0].X, 6);
        Assert.Equal(MarkerRejectionReason.DuplicateId, Assert.Single(outcome.Rejected).Reason);
        Assert.True(outcome.Suspicious);
    }

    [Fact]
    public void Validator_KeepsASmallQuadInsideABigOne_WhenItIsNotTheSameMarker()
    {
        var outcome = MarkerCandidateValidator.Validate([Quad(3, 0, 0, 300, 300), Quad(3, 120, 120, 60, 60)], MarkerDetectionOptions.Default, 1000, 1000);

        Assert.Single(outcome.Accepted);
        Assert.Equal(MarkerRejectionReason.DuplicateId, Assert.Single(outcome.Rejected).Reason);
    }

    private static MarkerCandidate Quad(int id, double x, double y, double w, double h) =>
        new(id, [new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h)]);

    private static SyntheticMarkerScene Scene(PlacedMarker[] markers) =>
        SyntheticMarkerScene.Create(
            900,
            500,
            markers,
            [new(0, 0), new(900, 0), new(900, 500), new(0, 500)],
            new Size(900, 500));
}
