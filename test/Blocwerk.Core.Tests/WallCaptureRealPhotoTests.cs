using System.Text.Json.Nodes;
using Blocwerk.HoldDetection.Markers;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The capture upload with the REAL OpenCV marker detector on real wall crops: the markers stored
/// and sent to the solver are exactly what the detector reports on the raw pixel grid, and the
/// photo size in the request is that same raw grid.
/// </summary>
public class WallCaptureRealPhotoTests
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Glyphs");

    [Fact]
    public async Task RealCrops_FlowIntoTheSolveRequest_WithRefinedPixelCorners()
    {
        var topLeft = await File.ReadAllBytesAsync(Path.Combine(Dir, "img2783-topleft.jpg"));
        var kickboard = await File.ReadAllBytesAsync(Path.Combine(Dir, "img2772-kickboard-seam.jpg"));
        var detector = new ArucoMarkerDetectionService();
        var direct = await detector.DetectAsync(topLeft, null, CancellationToken.None);

        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h, detector);
        await h.SeedWallAsync(holdCount: 0);
        await WallGlyphSettingsTests.Service(h).SetGlyphSettingsAsync(h.WallId, true, 125);
        var draft = await s.Service.CreateDraftAsync(h.WallId);
        var first = await s.Service.AddPhotoAsync(draft.CaptureId, "IMG_2783.jpg", ExifJpeg.Build(topLeft), CancellationToken.None);
        var second = await s.Service.AddPhotoAsync(draft.CaptureId, "IMG_2772.jpg", ExifJpeg.Build(kickboard, focal35: 24), CancellationToken.None);
        Assert.Equal([0, 5, 12], first.MarkerIds);
        Assert.NotEmpty(second.MarkerIds);

        var declarations = await s.Service.SuggestDeclarationsAsync(draft.CaptureId);
        Assert.Contains(declarations.Segments, d => d.Index == 0);
        Assert.Empty(await s.Service.StartAsync(draft.CaptureId, declarations, null));
        await s.Processor.ProcessAsync(draft.CaptureId, CancellationToken.None);

        var request = JsonNode.Parse(Assert.Single(s.Client.JsonSubmissions).Json)!;
        var photo = request["photos"]![0]!;
        Assert.Equal((390, 690), (photo["width"]!.GetValue<int>(), photo["height"]!.GetValue<int>()));
        var marker0 = photo["markers"]!.AsArray().Single(m => m!["id"]!.GetValue<int>() == 0)!;
        var expected = direct.Markers.Single(m => m.Id == 0).CornersPx;
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(expected[i].X, marker0["corners"]![i]![0]!.GetValue<double>(), 0.001);
            Assert.Equal(expected[i].Y, marker0["corners"]![i]![1]!.GetValue<double>(), 0.001);
        }

        Assert.Equal(24, request["photos"]![1]!["focal35mm"]!.GetValue<double>());
    }
}
