// <copyright file="WallBigUpdateEvidence3DTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests.Triage;

/// <summary>
/// The panel update's triage with the wall's 3D model, through the real big update: the new centre photo registers onto
/// facet "0" (1 px/mm on a 4000 × 3000 photo, so photo px (x, y) lands at a = x, b = 3000 − y). A second detection of an
/// old hold the matcher found again is discarded by default; one where an old hold was NOT found again is kept (it may be
/// a replacement). Only the quick review asks for 3D; without a matcher or a matching photo the triage is the photo-only one.
/// </summary>
public class WallBigUpdateEvidence3DTests
{
    private const double KnownX = 0.25;
    private const double NotFoundX = 0.35;
    private const double OffWallX = 0.95;
    private const double PlainX = 0.4;

    [Fact]
    public async Task WithTheModel_ASecondDetectionOfAHoldFoundAgain_IsDiscardedByDefault()
    {
        using var h = new WallTestHarness();
        var fake = await SeedAsync(h);

        var session = await ResumeAsync(h, new FixedPhotoTextureMatcher(fake, 10), use3D: true);

        var reasons = await ReasonsByXAsync(h, session);
        Assert.Equal(NewHoldDiscardReason.KnownHoldIn3D, reasons[KnownX]);
        Assert.Null(reasons[PlainX]);
        Assert.NotNull(session.Evidence3DModelId);
    }

    [Fact]
    public async Task ADetectionWhereAnOldHoldWasNotFoundAgain_IsKept_ItMayBeAReplacement()
    {
        using var h = new WallTestHarness();
        var fake = await SeedAsync(h);

        var session = await ResumeAsync(h, new FixedPhotoTextureMatcher(fake, 10), use3D: true);

        Assert.Null((await ReasonsByXAsync(h, session))[NotFoundX]);
    }

    [Fact]
    public async Task WithoutACameraPose_NothingIsCalledOffTheWall()
    {
        using var h = new WallTestHarness();
        var fake = await SeedAsync(h);

        var session = await ResumeAsync(h, new FixedPhotoTextureMatcher(fake, 10), use3D: true);

        Assert.Null((await ReasonsByXAsync(h, session))[OffWallX]);
    }

    [Fact]
    public async Task APlainResume_DoesNotMatchThePhotosToThe3DModel()
    {
        using var h = new WallTestHarness();
        var fake = await SeedAsync(h);

        var session = await ResumeAsync(h, new FixedPhotoTextureMatcher(fake, 10));

        Assert.Null(session.Evidence3DModelId);
        Assert.DoesNotContain(session.SuggestedNewDiscards!.Values, r => r == NewHoldDiscardReason.KnownHoldIn3D);
    }

    [Fact]
    public async Task AHoldPlacedOnAnEarlierModel_IsNotTakenAsEvidence()
    {
        using var h = new WallTestHarness();
        var fake = await SeedAsync(h, placedOnActiveModel: false);

        var session = await ResumeAsync(h, new FixedPhotoTextureMatcher(fake, 10), use3D: true);

        Assert.Null((await ReasonsByXAsync(h, session))[KnownX]);
    }

    [Fact]
    public async Task WithoutAMatcher_OrAPhotoThatDoesNotRegister_NothingChanges()
    {
        using var h = new WallTestHarness();
        var fake = await SeedAsync(h);

        var none = await ResumeAsync(h, null, use3D: true);
        var unreadable = await ResumeAsync(h, new FixedPhotoTextureMatcher(fake, 255), use3D: true);

        Assert.Empty(none.SuggestedNewDiscards!);
        Assert.Empty(unreadable.SuggestedNewDiscards!);
    }

    private static async Task<BigUpdateSession> ResumeAsync(WallTestHarness h, IPhotoTextureMatcher? matcher, bool use3D = false)
    {
        var files = Substitute.For<ICaptureFileStore>();
        files.ReadAsync("t0.jpg", Arg.Any<CancellationToken>()).Returns(new byte[] { 0 });
        var service = new WallBigUpdateService(
            h.DbContextFactory, h.CurrentUser, h.HoldDetection, PairingAtKnownX(),
            NullLogger<WallBigUpdateService>.Instance, textureMatcher: matcher, captureFiles: files);
        if (await StagedAsync(h) == 0)
        {
            await service.StageAsync(h.WallId, [new BigUpdatePhoto(CaptureScenario.TinyJpeg(), "image/jpeg", 0, 0)]);
        }

        return await service.ResumeAsync(h.WallId, use3D);
    }

    private static async Task<FakePhotoTextureMatcher> SeedAsync(WallTestHarness h, bool placedOnActiveModel = true)
    {
        await h.SeedWallAsync(holdCount: 0);
        h.HoldDetection.DetectHoldsAsync(Arg.Any<byte[]>(), Arg.Any<HoldDetectionParameters?>())
            .Returns(_ => Task.FromResult(new List<DetectedHold>
            {
                new(KnownX, 0.5, 0.01, null, 0.9),
                new(KnownX + 0.002, 0.5, 0.01, null, 0.9),
                new(NotFoundX + 0.002, 0.5, 0.01, null, 0.9),
                new(OffWallX, 0.5, 0.01, null, 0.9),
                new(PlainX, 0.3, 0.01, null, 0.9),
            }));
        await using var db = h.CreateContext();
        var live = new WallPanel { WallId = h.WallId, Col = 0, Row = 0, Generation = 0, Photo = CaptureScenario.TinyJpeg(1), PhotoContentType = "image/jpeg" };
        var model = new WallGeometryModel { WallId = h.WallId, Json = HoldPlacementScenario.TwoFacetJson, SchemaVersion = 1, Source = "test", IsActive = true };
        db.WallPanels.Add(live);
        db.WallGeometryModels.Add(model);
        db.WallGeometryTextures.Add(new WallGeometryTexture
        {
            GeometryModelId = model.Id, FacetId = "0", StoredPath = "t0.jpg", AMin = -100, AMax = 2100, BMin = -100, BMax = 3100, WidthPx = 2200, HeightPx = 3200,
        });
        var found = OldHold(h, live.Id, KnownX, 1000);
        var notFound = OldHold(h, live.Id, NotFoundX, 1400);
        db.Holds.AddRange(found, notFound);
        db.HoldPlacementRuns.Add(new HoldPlacementRun
        {
            WallId = h.WallId, GeometryModelId = placedOnActiveModel ? model.Id : Guid.NewGuid(), CreatedByUserId = h.Owner.Id,
            HoldsJson = HoldPlacementEntry.ToJson([new HoldPlacementEntry { HoldId = found.Id }, new HoldPlacementEntry { HoldId = notFound.Id }]),
        });
        await db.SaveChangesAsync();

        var fake = new FakePhotoTextureMatcher();
        fake.Views.Add(new FakeTextureView(10, 0, 0, 2000, 100, HoldPlacementScenario.Shift(99.5)));
        return fake;
    }

    private static async Task<int> StagedAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        return db.WallPanels.Count(p => p.WallId == h.WallId && p.StagedPhoto != null);
    }

    /// <summary>The suggested reason per unpaired staged detection, keyed by the spot it was placed near (null: kept).</summary>
    private static async Task<Dictionary<double, NewHoldDiscardReason?>> ReasonsByXAsync(WallTestHarness h, BigUpdateSession session)
    {
        await using var db = h.CreateContext();
        var staged = db.Holds.Where(x => x.WallId == h.WallId && x.Generation == 1).AsEnumerable().Where(x => Math.Abs(x.X - KnownX) > 1e-9).ToList();
        double[] spots = [KnownX, NotFoundX, OffWallX, PlainX];
        return staged.ToDictionary(
            x => spots.MinBy(s => Math.Abs(s - x.X)),
            x => session.SuggestedNewDiscards!.TryGetValue(x.Id, out var r) ? r : (NewHoldDiscardReason?)null);
    }

    private static Hold OldHold(WallTestHarness h, Guid panelId, double x, double a) => new()
    {
        WallId = h.WallId, WallPanelId = panelId, X = x, Y = 0.5, Radius = 0.01, Generation = 0, IsAutoDetected = true,
        FacetId = "0", PlaneAMm = a, PlaneBMm = 1500, WidthMm = 60, HeightMm = 60, MetricSource = HoldMetric.TextureRegistration,
    };

    /// <summary>The carryover matcher finds the old hold at <see cref="KnownX"/> again (its twin at the same spot) and nothing else.</summary>
    private static IHoldOverlapMatcher PairingAtKnownX()
    {
        var matcher = Substitute.For<IHoldOverlapMatcher>();
        matcher.Match(default!, default!, default!, default!, default).ReturnsForAnyArgs(ci =>
        {
            var left = ci.ArgAt<IReadOnlyList<MatcherHold>>(1);
            var right = ci.ArgAt<IReadOnlyList<MatcherHold>>(3);
            var old = left.FirstOrDefault(m => Math.Abs(m.X - KnownX) < 1e-9);
            var twin = right.FirstOrDefault(m => Math.Abs(m.X - KnownX) < 1e-9);
            var proposals = old is null || twin is null ? [] : new List<HoldOverlapProposal> { new(old.Id, twin.Id, 0.95, false, 1, null) };
            return new HoldOverlapResult(
                proposals,
                left.Where(m => m != old).Select(m => m.Id).ToList(),
                right.Where(m => m != twin).Select(m => m.Id).ToList());
        });
        return matcher;
    }
}
