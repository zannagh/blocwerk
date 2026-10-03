// <copyright file="MarkerPlanPrintDecodeTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.MarkerPlanning;
using Blocwerk.HoldDetection.Markers;

namespace Blocwerk.HoldDetection.Tests.MarkerPlanning;

/// <summary>
/// The strongest proof the printed glyphs are right: rasterise the plan's marker pages with the SAME
/// drawing code that writes the PDF and let the app's own detector read them back — every id must
/// decode to itself, once, at its true printed size. (This caught a real bug: cutting the payload
/// cells out one boolean op at a time lost holes, so the first PDFs printed wrong patterns.)
/// </summary>
public class MarkerPlanPrintDecodeTests
{
    /// <summary>60 dpi: a 50 mm marker is ≈ 118 px, a 125 mm one ≈ 295 px — photo-like scales.</summary>
    private const float Dpi = 60;

    private static readonly MarkerDetectionOptions AllIds = MarkerDetectionOptions.Default with
    {
        AllowedIds = Enumerable.Range(0, ArucoDict4X4.Count).ToHashSet(),
    };

    [Fact]
    public async Task EveryIdOfTheDictionary_PrintsAndDecodesToItself()
    {
        var markers = Enumerable.Range(0, ArucoDict4X4.Count)
            .Select(id => new PlanMarker(id, 0, 100 + ((id % 10) * 300), 100 + ((id / 10) * 300), 50, MarkerRole.Filler))
            .ToList();
        var plan = new MarkerPlan(
            MarkerPlan.CurrentSchemaVersion,
            ArucoDict4X4.DictionaryName,
            MarkerCameraPresets.Create(MarkerCameraPresets.Phone1X, 2000),
            [new PlanSegment(0, "test board", SegmentShape.Rectangle, 3200, 1600, TriangleCorner.BottomLeft, 0, 0, null)],
            markers);

        var decoded = await DecodeMarkerPagesAsync(plan);

        Assert.Equal(Enumerable.Range(0, ArucoDict4X4.Count), decoded.Select(d => d.Id).Order());
        var expectedPx = 50 * Dpi / 25.4;
        Assert.All(decoded, d => Assert.InRange(d.SidePx, expectedPx * 0.98, expectedPx * 1.02));
    }

    [Fact]
    public async Task OldSchemeIds_OnTheirPages_DecodeToThemselves()
    {
        // Ids 24–27 sit on segment 0 although the old scheme says segment 4: the plan carries them as-is.
        var plan = new MarkerPlan(
            MarkerPlan.CurrentSchemaVersion,
            ArucoDict4X4.DictionaryName,
            MarkerCameraPresets.Create(MarkerCameraPresets.PhoneUltraWide, 2500),
            [new PlanSegment(0, "main wall", SegmentShape.Rectangle, 5200, 3400, TriangleCorner.BottomLeft, 45, 0, null)],
            new[] { 24, 25, 26, 27 }.Select((id, i) => new PlanMarker(id, 0, 500 + (i * 1000), 500, 125, MarkerRole.Filler)).ToList());

        var decoded = await DecodeMarkerPagesAsync(plan);

        Assert.Equal([24, 25, 26, 27], decoded.Select(d => d.Id).Order());
    }

    private static async Task<List<DetectedMarker>> DecodeMarkerPagesAsync(MarkerPlan plan)
    {
        var detector = new ArucoMarkerDetectionService();
        var found = new List<DetectedMarker>();

        // The overview's map squares are not markers; only the true-size pages are read.
        for (var page = MarkerPlanPdf.FirstMarkerPage(plan); page < MarkerPlanPdf.PageCount(plan); page++)
        {
            var png = MarkerPlanPdf.RasterizePage(plan, "decode test", page, Dpi);
            var result = await detector.DetectAsync(png, AllIds, CancellationToken.None);

            // Page text can form a tiny look-alike; the validator must drop it as too small — nothing else.
            Assert.All(result.Rejected, r => Assert.Equal(MarkerRejectionReason.TooSmall, r.Reason));
            found.AddRange(result.Markers);
        }

        return found;
    }
}
