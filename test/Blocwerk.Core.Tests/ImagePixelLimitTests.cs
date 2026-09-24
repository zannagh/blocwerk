using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Helpers;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A few kilobytes can claim 20 000 × 20 000 pixels (1.2 GB decoded). The size is read from the header
/// and such an image is refused before any decoder allocates for it: a friendly error on a capture
/// upload, a silent skip in hold enrichment (which must never break ingest).
/// </summary>
public class ImagePixelLimitTests
{
    [Fact]
    public void HeaderSize_DecidesWithoutDecoding()
    {
        Assert.True(ImagePixelLimit.IsTooLarge(HostileImages.PngClaiming(20_000, 20_000)));
        Assert.False(ImagePixelLimit.IsTooLarge(HostileImages.PngClaiming(8_000, 6_000)));
        Assert.False(ImagePixelLimit.IsTooLarge(CaptureScenario.TinyJpeg()));
        Assert.False(ImagePixelLimit.IsTooLarge([1, 2, 3]));
        Assert.Throws<ArgumentException>(() => ImagePixelLimit.EnsureDecodable(HostileImages.PngClaiming(20_000, 20_000), "image"));
    }

    [Fact]
    public async Task CaptureUpload_AboveTheLimit_IsRefusedWithAFriendlyMessage()
    {
        using var h = new WallTestHarness();
        var detector = Substitute.For<IMarkerDetectionService>();
        using var s = new CaptureScenario(h, detector);
        await h.SeedWallAsync(holdCount: 0);
        await WallGlyphSettingsTests.Service(h).SetGlyphSettingsAsync(h.WallId, true, 125);
        var draft = await s.Service.CreateDraftAsync(h.WallId);

        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => s.Service.AddPhotoAsync(
            draft.CaptureId, "huge.png", HostileImages.PngClaiming(20_000, 20_000), CancellationToken.None));

        Assert.Contains("megapixels", error.Message);
        await detector.DidNotReceiveWithAnyArgs().DetectAsync(default!, default, default);
        await using var db = h.CreateContext();
        Assert.Empty(await db.WallCapturePhotos.ToListAsync());
    }

    [Fact]
    public async Task Enrichment_AboveTheLimit_IsSkipped_WithoutDecoding()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var outlines = Substitute.For<IHoldOutlineService>();
        var markers = Substitute.For<IMarkerDetectionService>();
        var service = EnrichmentFakes.Service(outlines, markers);
        await using var db = h.CreateContext();
        var wall = await db.Walls.SingleAsync(w => w.Id == h.WallId);
        wall.GlyphsEnabled = true;
        var hold = EnrichmentFakes.AutoHold(h.WallId, 0.5, 0.5);
        db.Holds.Add(hold);

        var summary = await service.EnrichAsync(
            db, new HoldEnrichmentRequest(HostileImages.PngClaiming(20_000, 20_000), wall, [hold]));

        Assert.Same(HoldEnrichmentSummary.None, summary);
        outlines.DidNotReceiveWithAnyArgs().OpenSession(default(byte[])!);
        await markers.DidNotReceiveWithAnyArgs().DetectAsync(default!, default, default);
        Assert.Null(hold.ShapePoints);
    }
}
