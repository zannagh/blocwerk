// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Core.Services.PanelCrop;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SkiaSharp;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Cropping a live panel photo in place: holds keep their wall position, a crop that cuts holds off needs confirming and
/// lists the boulders, "crop anyway" removes them by the hold-removal rules, undo restores the original exactly, the
/// marker corners and proposals follow, the 3D re-placement is queued, and the photo's cache tag moves.
/// </summary>
public class PanelCropServiceTests
{
    private const double Tolerance = 1e-9;

    private static readonly PanelCropRect KeepsAll = new(0.1, 0.1, 0.875, 0.8);

    private static readonly PanelCropRect CutsEdge = new(0.25, 0, 0.5, 1);

    [Fact]
    public async Task Crop_WaitsForAnotherWriterOfTheWallsHolds()
    {
        using var f = new PanelCropFixture();
        await f.SeedAsync();

        Task<PanelCropResult> crop;
        using (WallHoldWriteLock.TryAcquire(f.Harness.WallId, "busy"))
        {
            crop = f.Service.CropAsync(f.Harness.WallId, f.PanelId, KeepsAll, confirmRemovals: false);
            await Task.Delay(300);
            Assert.False(crop.IsCompleted);
        }

        Assert.True((await crop).Applied);
    }

    [Fact]
    public async Task Crop_RemapsHoldsInPlace_AndKeepsTheOriginal()
    {
        using var f = new PanelCropFixture();
        await f.SeedAsync();

        var result = await f.Service.CropAsync(f.Harness.WallId, f.PanelId, KeepsAll, confirmRemovals: false);

        Assert.True(result.Applied);
        Assert.Equal(1, result.PhotoRevision);
        var panel = await f.LoadPanelAsync();
        Assert.Equal(0, panel.Generation);
        using (var bitmap = SKBitmap.Decode(panel.Photo))
        {
            Assert.Equal(350, bitmap.Width);
            Assert.Equal(240, bitmap.Height);
        }

        var centre = await f.LoadHoldAsync(f.Centre.Id);
        Assert.Equal(0.5, 0.1 + (centre.X * 0.875), Tolerance);
        Assert.Equal(0.5, 0.1 + (centre.Y * 0.8), Tolerance);
        Assert.Equal(f.Centre.ShapePoints![0].Dx / 0.875, centre.ShapePoints![0].Dx, Tolerance);
        Assert.Equal("0", centre.FacetId);
        Assert.Equal(1200, centre.PlaneAMm);

        await using var db = f.Harness.CreateContext();
        var crop = await db.WallPanelCrops.SingleAsync(c => c.WallPanelId == f.PanelId);
        Assert.Equal(f.OriginalPhoto, crop.OriginalPhoto);
        Assert.Equal(1, await db.Holds.CountAsync(h => h.Id == f.Edge.Id));
    }

    [Fact]
    public async Task Crop_KeepsAHoldThatStillReachesIn_WithItsCentreOutside_AndUndoRestoresIt()
    {
        using var f = new PanelCropFixture();
        await f.SeedAsync();
        var edgeCrop = new PanelCropRect(0, 0, 0.94, 1); // the edge hold's centre (0.95) falls outside, its outline reaches in

        var preview = await f.Service.PreviewAsync(f.Harness.WallId, f.PanelId, edgeCrop);
        var result = await f.Service.CropAsync(f.Harness.WallId, f.PanelId, edgeCrop, confirmRemovals: false);

        Assert.Empty(preview.RemovedHoldIds);
        Assert.True(result.Applied);
        var kept = await f.LoadHoldAsync(f.Edge.Id);
        Assert.Equal(0.95 / 0.94, kept.X, Tolerance);
        Assert.True(kept.X > 1);

        await f.Service.UndoAsync(f.Harness.WallId, f.PanelId);
        Assert.Equal(0.95, (await f.LoadHoldAsync(f.Edge.Id)).X, Tolerance);
    }

    [Fact]
    public async Task Crop_ThatCutsOffHolds_NeedsConfirmation_AndListsTheBoulders()
    {
        using var f = new PanelCropFixture();
        await f.SeedAsync();

        var preview = await f.Service.PreviewAsync(f.Harness.WallId, f.PanelId, CutsEdge);
        var result = await f.Service.CropAsync(f.Harness.WallId, f.PanelId, CutsEdge, confirmRemovals: false);

        Assert.True(preview.NeedsConfirmation);
        Assert.Equal([f.Edge.Id], preview.RemovedHoldIds);
        Assert.Equal(1, preview.KeptHoldCount);
        var boulder = Assert.Single(preview.Boulders);
        Assert.Equal(("Edge Problem", true), (boulder.Name, boulder.IsActive));

        Assert.False(result.Applied);
        var panel = await f.LoadPanelAsync();
        Assert.Equal(f.OriginalPhoto, panel.Photo);
        Assert.Equal(0, panel.PhotoRevision);
        Assert.Equal(0.5, (await f.LoadHoldAsync(f.Centre.Id)).X);
    }

    [Fact]
    public async Task CropAnyway_RemovesTheCutHolds_ByTheHoldRemovalRules()
    {
        using var f = new PanelCropFixture();
        await f.SeedAsync();

        var result = await f.Service.CropAsync(f.Harness.WallId, f.PanelId, CutsEdge, confirmRemovals: true);

        Assert.True(result.Applied);
        Assert.Equal(1, result.HistoricBoulderCount);
        await using var db = f.Harness.CreateContext();
        Assert.False(await db.Holds.AnyAsync(h => h.Id == f.Edge.Id));
        Assert.False(await db.HoldLinks.AnyAsync());
        Assert.False(await db.BoulderHolds.AnyAsync(bh => bh.BoulderId == f.BoulderId));
        Assert.True((await db.Boulders.SingleAsync(b => b.Id == f.BoulderId)).IsHistoric);
    }

    [Fact]
    public async Task Undo_RestoresTheOriginalExactly_AndMapsTheHoldsBack()
    {
        using var f = new PanelCropFixture();
        await f.SeedAsync();
        await f.Service.CropAsync(f.Harness.WallId, f.PanelId, KeepsAll, confirmRemovals: false);
        await f.Service.CropAsync(f.Harness.WallId, f.PanelId, new PanelCropRect(0.1, 0.1, 0.8, 0.8), confirmRemovals: true);

        var result = await f.Service.UndoAsync(f.Harness.WallId, f.PanelId);

        Assert.Equal(3, result.PhotoRevision);
        var panel = await f.LoadPanelAsync();
        Assert.Equal(f.OriginalPhoto, panel.Photo);
        Assert.Equal("image/jpeg", panel.PhotoContentType);
        var centre = await f.LoadHoldAsync(f.Centre.Id);
        Assert.Equal(f.Centre.X, centre.X, Tolerance);
        Assert.Equal(f.Centre.Y, centre.Y, Tolerance);
        Assert.Equal(f.Centre.Radius, centre.Radius, Tolerance);
        Assert.All(f.Centre.ShapePoints!.Zip(centre.ShapePoints!), p => Assert.Equal(p.First.Dy, p.Second.Dy, Tolerance));

        await using var db = f.Harness.CreateContext();
        Assert.False(await db.WallPanelCrops.AnyAsync());
        Assert.Null((await f.Service.GetStateAsync(f.Harness.WallId, f.PanelId))!.Crop);
    }

    [Fact]
    public async Task Crop_MovesTheMarkerCorners_AndThePendingProposals()
    {
        using var f = new PanelCropFixture();
        await f.SeedAsync();

        await f.Service.CropAsync(f.Harness.WallId, f.PanelId, KeepsAll, confirmRemovals: false);

        await using var db = f.Harness.CreateContext();
        var marker = await db.WallMarkerObservations.SingleAsync(o => o.Id == f.MarkerId);
        var corners = JsonSerializer.Deserialize<double[][]>(marker.CornersJson)!;
        Assert.Equal((0.4 - 0.1) / 0.875, corners[0][0], Tolerance);
        Assert.Equal((0.6 - 0.1) / 0.8, corners[2][1], Tolerance);
        Assert.Equal(80, marker.SidePx);

        var proposal = await db.HoldProposals.SingleAsync(p => p.Id == f.ProposalId);
        Assert.Equal((0.3 - 0.1) / 0.875, proposal.PanelX!.Value, Tolerance);
        Assert.Equal((0.6 - 0.1) / 0.8, proposal.PanelY!.Value, Tolerance);
    }

    [Fact]
    public async Task Crop_QueuesTheKeptHolds_For3DReplacement()
    {
        using var f = new PanelCropFixture();
        await f.SeedAsync();

        await f.Service.CropAsync(f.Harness.WallId, f.PanelId, CutsEdge, confirmRemovals: true);

        f.Queue.Received(1).Enqueue(f.Harness.WallId, Arg.Is<IEnumerable<Guid>>(ids => ids.Single() == f.Centre.Id));
    }

    [Fact]
    public async Task Crop_MovesThePhotoTag_SoCachesSeeANewPhoto()
    {
        using var f = new PanelCropFixture();
        await f.SeedAsync();
        var panels = new WallPanelService(
            f.Harness.DbContextFactory, f.Harness.CurrentUser, f.Harness.HoldDetection, Substitute.For<IHoldOverlapMatcher>(), NullLogger<WallPanelService>.Instance);
        var before = await panels.GetPanelPhotoTagAsync(f.Harness.WallId, f.PanelId);

        await f.Service.CropAsync(f.Harness.WallId, f.PanelId, KeepsAll, confirmRemovals: false);
        var cropped = await panels.GetPanelPhotoTagAsync(f.Harness.WallId, f.PanelId);
        await f.Service.UndoAsync(f.Harness.WallId, f.PanelId);
        var undone = await panels.GetPanelPhotoTagAsync(f.Harness.WallId, f.PanelId);

        Assert.Equal(0L, before!.Version);
        Assert.NotEqual(before.Version, cropped!.Version);
        Assert.NotEqual(cropped.Version, undone!.Version);
        Assert.Equal(before.Length, undone.Length);
    }

    [Fact]
    public async Task CropAndUndo_AreJournalledAsNamedWallBatches()
    {
        using var f = new PanelCropFixture();
        await f.SeedAsync();
        var journal = new ChangeJournal();
        var service = new PanelCropService(
            new JournallingDbContextFactory(f.Harness.DbContextFactory.ConnectionString, journal), f.Harness.CurrentUser, NullLogger<PanelCropService>.Instance, changeJournal: journal);

        await service.CropAsync(f.Harness.WallId, f.PanelId, CutsEdge, confirmRemovals: true);
        await service.UndoAsync(f.Harness.WallId, f.PanelId);

        await using var db = f.Harness.CreateContext();
        var crop = await db.ChangeJournalBatches.SingleAsync(b => b.Label == "panel-crop");
        var undo = await db.ChangeJournalBatches.SingleAsync(b => b.Label == "panel-crop-undo");
        Assert.Equal(f.Harness.WallId, crop.ScopeId);
        var cropTypes = await db.ChangeJournalEntries.Where(e => e.BatchId == crop.Id).Select(e => e.EntityType).Distinct().ToListAsync();
        Assert.Contains(nameof(WallPanel), cropTypes);
        Assert.Contains(nameof(WallPanelCrop), cropTypes);
        Assert.Contains(nameof(Hold), cropTypes);
        Assert.True(await db.ChangeJournalEntries.AnyAsync(e => e.BatchId == undo.Id && e.EntityType == nameof(WallPanelCrop)));
    }

    [Fact]
    public async Task Crop_ByANonAdmin_IsRefused()
    {
        using var f = new PanelCropFixture();
        await f.SeedAsync();
        f.Harness.ActingUser = await f.Harness.AddMemberAsync("member@test", WallRole.Moderator);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => f.Service.CropAsync(f.Harness.WallId, f.PanelId, KeepsAll, confirmRemovals: true));
        Assert.Equal(f.OriginalPhoto, (await f.LoadPanelAsync()).Photo);
    }

    [Fact]
    public async Task Crop_DuringAWallUpdate_IsRefused()
    {
        using var f = new PanelCropFixture();
        await f.SeedAsync();
        await using (var db = f.Harness.CreateContext())
        {
            db.WallUpdateSessions.Add(new WallUpdateSession { WallId = f.Harness.WallId, StagedGeneration = 1 });
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Service.CropAsync(f.Harness.WallId, f.PanelId, KeepsAll, confirmRemovals: true));
    }
}
