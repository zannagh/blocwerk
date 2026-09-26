// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json.Nodes;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Capture.Corrections;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Corrections;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The three model corrections (<see cref="WallGeometryCorrectionService"/>): each makes a NEW active model version derived
/// from the old one, with the old one's texture and photo-real files reused under mapped bounds and frame, the capture
/// pointed at it and its follow-up chain queued; the old version stays in the history.
/// </summary>
public class GeometryCorrectionTests
{
    [Fact]
    public async Task MakeSizesExact_ScalesTheModelTexturesAndSplat_IntoANewActiveVersion()
    {
        using var h = new WallTestHarness();
        var (oldId, captureId) = await GeometryCorrectionFixture.SeedAsync(h);
        var queue = new CorrectionFollowUpQueue();

        // The model has 1800 mm between the two points; the wall has 1980 mm.
        var result = await GeometryCorrectionFixture.Service(h, queue)
            .MakeSizesExactAsync(h.WallId, new CaptureScaleReference(1, [1000, 750], [1600, 750], 1980));

        Assert.Equal(1.1, result.Scale, 6);
        Assert.Equal(oldId, result.PreviousModelId);
        await using var db = h.CreateContext();
        var model = await db.WallGeometryModels.SingleAsync(m => m.IsActive);
        Assert.Equal(result.ModelId, model.Id);
        Assert.Equal(oldId, model.DerivedFromModelId);
        Assert.Equal("correction scale", model.Source);
        Assert.False((await db.WallGeometryModels.SingleAsync(m => m.Id == oldId)).IsActive);

        var doc = WallGeometryDocument.Parse(model.Json);
        Assert.Equal("measured", doc.World!.ScaleSource);
        Assert.True(doc.World.ScaleKnown);
        Assert.Equal(3300, doc.FindFacet("0")!.Value.Facet.ExtentMm!.Value.AMax, 0);
        Assert.Equal("scale", JsonNode.Parse(model.Json)!["quality"]!["correction"]!["kind"]!.GetValue<string>());

        var textures = await db.WallGeometryTextures.Where(t => t.GeometryModelId == model.Id).OrderBy(t => t.FacetId).ToListAsync();
        Assert.Equal(["tex0.jpg", "tex1.jpg"], textures.Select(t => t.StoredPath));
        Assert.Equal(3300, textures[0].AMax, 6);
        var splat = await db.WallGeometrySplats.SingleAsync(s => s.GeometryModelId == model.Id);
        Assert.Equal("wall.spz", splat.StoredPath);
        Assert.Equal(550, GeometryJson.Matrix4(JsonNode.Parse(splat.FrameJson)!["toWorldMm"])![0], 6);

        var capture = await db.WallCaptures.SingleAsync(c => c.Id == captureId);
        Assert.Equal(model.Id, capture.GeometryModelId);
        Assert.Null(capture.FollowUpJson);
        Assert.Equal(captureId, await Dequeued(queue));
    }

    [Fact]
    public async Task ThisSurfaceIsVertical_TurnsTheModelUntilItIsPlumb()
    {
        using var h = new WallTestHarness();
        await GeometryCorrectionFixture.SeedAsync(h, GeometryCorrectionFixture.LeaningDoc());
        var service = GeometryCorrectionFixture.Service(h, new CorrectionFollowUpQueue());
        Assert.False((await service.GetStateAsync(h.WallId))!.GravityKnown);

        var result = await service.SetVerticalSurfaceAsync(h.WallId, "0");

        Assert.Equal(3, result.RotationDeg, 2);
        await using var db = h.CreateContext();
        var doc = WallGeometryDocument.Parse((await db.WallGeometryModels.SingleAsync(m => m.IsActive)).Json);
        Assert.Equal(new double[] { 0, 0, 1 }, doc.World!.Up);
        Assert.True(doc.World.GravityKnown);
        Assert.Equal("declared", doc.World.GravitySource);
        Assert.Equal(0, doc.FindFacet("0")!.Value.Facet.MeasuredAngleDeg!.Value, 2);
        Assert.Equal(0, doc.FindFacet("1")!.Value.Facet.MeasuredAngleDeg!.Value, 2);
        Assert.True(doc.FindFacet("0")!.Value.Segment.AngleIsGravityReference);
        WallGeometryModelTransformerTests.AssertClose([0, -1, 0], doc.FindFacet("0")!.Value.Facet.Normal!, 1e-5);

        var state = (await service.GetStateAsync(h.WallId))!;
        Assert.True(state.GravityKnown);
        Assert.Equal("from the declared vertical surface", state.Gravity);
        Assert.True(state.Facets.Single(f => f.Id == "0").IsVertical);
    }

    [Fact]
    public async Task NotPartOfTheWall_DropsTheSurfaceAndItsTexture()
    {
        using var h = new WallTestHarness();
        await GeometryCorrectionFixture.SeedAsync(h);

        await GeometryCorrectionFixture.Service(h, new CorrectionFollowUpQueue()).DropSurfaceAsync(h.WallId, "1");

        await using var db = h.CreateContext();
        var model = await db.WallGeometryModels.SingleAsync(m => m.IsActive);
        Assert.Equal(["0"], WallGeometryDocument.Parse(model.Json).Segments.SelectMany(s => s.Facets).Select(f => f.Id));
        Assert.Equal(["0"], await db.WallGeometryTextures.Where(t => t.GeometryModelId == model.Id).Select(t => t.FacetId).ToListAsync());
        Assert.Equal(2, await db.WallGeometryModels.CountAsync());
    }

    [Fact]
    public async Task Refusals_ChangeNothing()
    {
        using var h = new WallTestHarness();
        var (oldId, _) = await GeometryCorrectionFixture.SeedAsync(h);
        var service = GeometryCorrectionFixture.Service(h, new CorrectionFollowUpQueue());

        await Refused(() => service.DropSurfaceAsync(h.WallId, "0"), "defines the model's frame");
        await Refused(() => service.DropSurfaceAsync(h.WallId, "9"), "no such surface");
        await Refused(() => service.SetVerticalSurfaceAsync(h.WallId, "9"), "no such surface");
        await Refused(
            () => service.MakeSizesExactAsync(h.WallId, new CaptureScaleReference(1, [1000, 10], [1000, 20], 500)), "not on any surface");
        await Refused(
            () => service.MakeSizesExactAsync(h.WallId, new CaptureScaleReference(1, [1000, 750], [1010, 750], 50)), "too close together");
        await Refused(
            () => service.MakeSizesExactAsync(h.WallId, new CaptureScaleReference(1, [1000, 750], [1600, 750], 9000)), "more than half");
        await Refused(
            () => service.MakeSizesExactAsync(h.WallId, new CaptureScaleReference(7, [1000, 750], [1600, 750], 1800)), "no longer stored");

        await using var db = h.CreateContext();
        Assert.Equal(oldId, (await db.WallGeometryModels.SingleAsync()).Id);
    }

    [Fact]
    public async Task Guards_NonAdminKioskRunningCaptureMarkerModelAndNoModel()
    {
        using var h = new WallTestHarness();
        await GeometryCorrectionFixture.SeedAsync(h, status: WallCaptureStatus.Succeeded);
        var queue = new CorrectionFollowUpQueue();
        var kiosk = Substitute.For<IKioskContext>();
        kiosk.IsKiosk.Returns(true);
        kiosk.KioskWallId.Returns(h.WallId);
        await Assert.ThrowsAsync<KioskRestrictedException>(
            () => GeometryCorrectionFixture.Service(h, queue, kiosk).DropSurfaceAsync(h.WallId, "1"));

        await using (var db = h.CreateContext())
        {
            db.WallCaptures.Add(new WallCapture { WallId = h.WallId, CreatedByUserId = h.Owner.Id, Status = WallCaptureStatus.Solving });
            await db.SaveChangesAsync();
        }

        await Refused(() => GeometryCorrectionFixture.Service(h, queue).DropSurfaceAsync(h.WallId, "1"), "still being processed");

        h.ActingUser = await h.AddMemberAsync("climber@test", WallRole.Member);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => GeometryCorrectionFixture.Service(h, queue).GetStateAsync(h.WallId));

        using var markers = new WallTestHarness();
        await GeometryCorrectionFixture.SeedAsync(markers, MarkerlessFixture.MarkerDoc(), features: false);
        await Refused(
            () => GeometryCorrectionFixture.Service(markers, queue)
                .MakeSizesExactAsync(markers.WallId, new CaptureScaleReference(1, [1000, 750], [1600, 750], 1800)),
            "printed markers");

        using var empty = new WallTestHarness();
        await empty.SeedWallAsync(holdCount: 0);
        Assert.Null(await GeometryCorrectionFixture.Service(empty, queue).GetStateAsync(empty.WallId));
        await Refused(() => GeometryCorrectionFixture.Service(empty, queue).DropSurfaceAsync(empty.WallId, "1"), "no 3D model");
    }

    [Fact]
    public async Task RevertingToTheOldVersion_PointsTheCaptureBackAtIt()
    {
        using var h = new WallTestHarness();
        var (oldId, captureId) = await GeometryCorrectionFixture.SeedAsync(h);
        var queue = new CorrectionFollowUpQueue();
        await GeometryCorrectionFixture.Service(h, queue).DropSurfaceAsync(h.WallId, "1");
        await Dequeued(queue);
        var glyphs = new WallGlyphService(h.DbContextFactory, h.CurrentUser, NullLogger<WallGlyphService>.Instance, null, queue);

        await glyphs.ActivateGeometryAsync(oldId);

        await using var db = h.CreateContext();
        Assert.Equal(oldId, (await db.WallCaptures.SingleAsync(c => c.Id == captureId)).GeometryModelId);
        Assert.Equal(captureId, await Dequeued(queue));

        // The files the two versions share stay referenced while either row exists.
        var shared = await SharedCaptureFiles.ReferencedAsync(db, CancellationToken.None);
        Assert.Contains("tex1.jpg", shared);
        Assert.Contains("wall.spz", shared);
    }

    internal static async Task Refused(Func<Task> action, string reason)
    {
        var ex = await Assert.ThrowsAsync<UserFacingException>(action);
        Assert.Contains(reason, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<Guid> Dequeued(CorrectionFollowUpQueue queue)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        return await queue.DequeueAsync(cts.Token);
    }
}
