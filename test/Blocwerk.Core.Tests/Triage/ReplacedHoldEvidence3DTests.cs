// <copyright file="ReplacedHoldEvidence3DTests.cs" company="Blocwerk">
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
/// A replaced hold on a wall whose holds are placed on the 3D model (same registration as
/// <see cref="WallBigUpdateEvidence3DTests"/>). The triage's "the model knows a hold here" only counts old holds that
/// are still paired, so the detection freed from a replaced pair is not dropped for the very hold it replaced: it is a
/// kept new hold from the start, with no correction after the triage. While the pair stands, a second detection on the
/// spot is dropped as known.
/// </summary>
public class ReplacedHoldEvidence3DTests
{
    private const double X = 0.25;

    private static readonly (double X, double Y)[] Anchors = [(0.1, 0.2), (0.9, 0.2), (0.5, 0.9)];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheFreedDetection_IsNotDroppedAsAHoldTheModelKnows(bool replaced)
    {
        using var h = new WallTestHarness();
        var (old, fake) = await SeedAsync(h);
        var probe = new ScriptedPresenceProbe();
        if (replaced)
        {
            probe.Set(X, 0.5, 0.1);
        }

        var files = Substitute.For<ICaptureFileStore>();
        files.ReadAsync("t0.jpg", Arg.Any<CancellationToken>()).Returns(new byte[] { 0 });
        var service = new WallBigUpdateService(
            h.DbContextFactory, h.CurrentUser, h.HoldDetection, new SameFrameHoldMatcher(), NullLogger<WallBigUpdateService>.Instance,
            presenceProbe: probe, textureMatcher: new FixedPhotoTextureMatcher(fake, 10), captureFiles: files);
        await service.StageAsync(h.WallId, [new BigUpdatePhoto(CaptureScenario.TinyJpeg(), "image/jpeg", 0, 0)]);

        var session = await service.ResumeAsync(h.WallId, use3DEvidence: true);

        await using var db = h.CreateContext();
        var atSpot = db.Holds.Where(x => x.WallId == h.WallId && x.Generation == 1).AsEnumerable().Where(x => Math.Abs(x.X - X) < 0.01).Select(x => x.Id).ToList();
        var reasons = atSpot.Where(session.SuggestedNewDiscards!.ContainsKey).Select(id => session.SuggestedNewDiscards[id]).ToList();
        Assert.NotNull(session.Evidence3DModelId);
        if (replaced)
        {
            Assert.Equal(HoldMoveOutcome.Replaced, session.NotFoundAgain![old]);
            Assert.Empty(reasons);
        }
        else
        {
            Assert.Empty(session.NotFoundAgain!);
            Assert.Equal([NewHoldDiscardReason.KnownHoldIn3D], reasons);
        }
    }

    /// <summary>Three anchors and one hold at <see cref="X"/> placed on facet "0"; the new photo detects all of them, the placed one twice.</summary>
    private static async Task<(Guid Old, FakePhotoTextureMatcher Fake)> SeedAsync(WallTestHarness h)
    {
        await h.SeedWallAsync(holdCount: 0);
        (double X, double Y)[] spots = [.. Anchors, (X, 0.5), (X + 0.002, 0.5)];
        h.HoldDetection.DetectHoldsAsync(Arg.Any<byte[]>(), Arg.Any<HoldDetectionParameters?>())
            .Returns(_ => Task.FromResult(spots.Select(s => new DetectedHold(s.X, s.Y, 0.01, null, 0.9)).ToList()));
        await using var db = h.CreateContext();
        var live = new WallPanel { WallId = h.WallId, Col = 0, Row = 0, Generation = 0, Photo = CaptureScenario.TinyJpeg(1), PhotoContentType = "image/jpeg" };
        var model = new WallGeometryModel { WallId = h.WallId, Json = HoldPlacementScenario.TwoFacetJson, SchemaVersion = 1, Source = "test", IsActive = true };
        db.WallPanels.Add(live);
        db.WallGeometryModels.Add(model);
        db.WallGeometryTextures.Add(new WallGeometryTexture
        {
            GeometryModelId = model.Id, FacetId = "0", StoredPath = "t0.jpg", AMin = -100, AMax = 2100, BMin = -100, BMax = 3100, WidthPx = 2200, HeightPx = 3200,
        });
        Hold Old(double x, double y) => new() { WallId = h.WallId, WallPanelId = live.Id, X = x, Y = y, Radius = 0.01, Generation = 0, IsAutoDetected = true };
        var placed = Old(X, 0.5);
        (placed.FacetId, placed.PlaneAMm, placed.PlaneBMm, placed.WidthMm, placed.HeightMm, placed.MetricSource) =
            ("0", 1000, 1500, 60, 60, HoldMetric.TextureRegistration);
        db.Holds.AddRange(Anchors.Select(a => Old(a.X, a.Y)));
        db.Holds.Add(placed);
        db.HoldPlacementRuns.Add(new HoldPlacementRun
        {
            WallId = h.WallId, GeometryModelId = model.Id, CreatedByUserId = h.Owner.Id,
            HoldsJson = HoldPlacementEntry.ToJson([new HoldPlacementEntry { HoldId = placed.Id }]),
        });
        await db.SaveChangesAsync();

        var fake = new FakePhotoTextureMatcher();
        fake.Views.Add(new FakeTextureView(10, 0, 0, 2000, 100, HoldPlacementScenario.Shift(99.5)));
        return (placed.Id, fake);
    }
}
