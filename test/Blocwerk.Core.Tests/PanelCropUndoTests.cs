// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Services;
using Blocwerk.Core.Services.PanelCrop;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// "Undo crop" through the change journal: when nothing touched the crop's rows since, the whole crop is reverted, the
/// removed holds, their link and boulder membership come back and the boulder is active again. When a hold was edited
/// since, the journal refuses and the undo falls back to the photo only, reporting the holds it could not restore.
/// </summary>
public class PanelCropUndoTests
{
    private static readonly PanelCropRect CutsEdge = new(0.25, 0, 0.5, 1);

    [Fact]
    public async Task Undo_WithNoEditsSince_RevertsTheWholeCrop_HoldsAndBouldersIncluded()
    {
        using var f = new PanelCropFixture();
        await f.SeedAsync();
        var service = JournalledService(f);
        await service.CropAsync(f.Harness.WallId, f.PanelId, CutsEdge, confirmRemovals: true);
        await service.CropAsync(f.Harness.WallId, f.PanelId, new PanelCropRect(0.1, 0.1, 0.8, 0.8), confirmRemovals: true);
        Assert.Equal(1, (await service.GetStateAsync(f.Harness.WallId, f.PanelId))!.RemovedHoldCount);

        var result = await service.UndoAsync(f.Harness.WallId, f.PanelId);

        Assert.True(result.RevertedFromJournal);
        Assert.Equal(0, result.HoldsNotRestored);
        Assert.Equal(0, result.PhotoRevision);
        var panel = await f.LoadPanelAsync();
        Assert.Equal(f.OriginalPhoto, panel.Photo);
        Assert.Equal(f.Centre.X, (await f.LoadHoldAsync(f.Centre.Id)).X, 12);
        var edge = await f.LoadHoldAsync(f.Edge.Id);
        Assert.Equal((f.Edge.X, f.Edge.Y), (edge.X, edge.Y));

        await using var db = f.Harness.CreateContext();
        Assert.True(await db.HoldLinks.AnyAsync(l => l.HoldAId == f.Edge.Id));
        Assert.True(await db.BoulderHolds.AnyAsync(bh => bh.BoulderId == f.BoulderId && bh.HoldId == f.Edge.Id));
        Assert.False((await db.Boulders.SingleAsync(b => b.Id == f.BoulderId)).IsHistoric);
        Assert.False(await db.WallPanelCrops.AnyAsync());
        Assert.Equal(2, await db.ChangeJournalBatches.CountAsync(b => b.Label == "revert:panel-crop"));
    }

    [Fact]
    public async Task Undo_AfterAHoldWasEdited_FallsBackToThePhoto_AndSaysWhatStaysRemoved()
    {
        using var f = new PanelCropFixture();
        await f.SeedAsync();
        var service = JournalledService(f);
        await service.CropAsync(f.Harness.WallId, f.PanelId, CutsEdge, confirmRemovals: true);
        await using (var edit = new JournallingDbContextFactory(f.Harness.DbContextFactory.ConnectionString, new ChangeJournal()).CreateDbContext())
        {
            var centre = await edit.Holds.SingleAsync(h => h.Id == f.Centre.Id);
            centre.Radius += 0.01; // a property the crop itself rewrote, so the journal sees the divergence
            await edit.SaveChangesAsync();
        }

        var result = await service.UndoAsync(f.Harness.WallId, f.PanelId);

        Assert.False(result.RevertedFromJournal);
        Assert.Equal(1, result.HoldsNotRestored);
        Assert.Equal(f.OriginalPhoto, (await f.LoadPanelAsync()).Photo);
        var centreAfter = await f.LoadHoldAsync(f.Centre.Id);
        Assert.Equal(f.Centre.X, centreAfter.X, 9);
        Assert.Equal(((f.Centre.Radius / Math.Sqrt(0.5)) + 0.01) * Math.Sqrt(0.5), centreAfter.Radius, 9);
        await using var db = f.Harness.CreateContext();
        Assert.False(await db.Holds.AnyAsync(h => h.Id == f.Edge.Id));
        Assert.True((await db.Boulders.SingleAsync(b => b.Id == f.BoulderId)).IsHistoric);
        Assert.False(await db.WallPanelCrops.AnyAsync());
    }

    [Fact]
    public async Task Undo_WithoutAJournal_RestoresThePhotoOnly()
    {
        using var f = new PanelCropFixture();
        await f.SeedAsync();
        await f.Service.CropAsync(f.Harness.WallId, f.PanelId, CutsEdge, confirmRemovals: true);

        var result = await f.Service.UndoAsync(f.Harness.WallId, f.PanelId);

        Assert.False(result.RevertedFromJournal);
        Assert.Equal(f.OriginalPhoto, (await f.LoadPanelAsync()).Photo);
    }

    /// <summary>The service as production wires it: journalled contexts and the journal reverter over them.</summary>
    private static PanelCropService JournalledService(PanelCropFixture f)
    {
        var journal = new ChangeJournal();
        var factory = new JournallingDbContextFactory(f.Harness.DbContextFactory.ConnectionString, journal);
        return new PanelCropService(
            factory, f.Harness.CurrentUser, NullLogger<PanelCropService>.Instance,
            changeJournal: journal, refinementQueue: f.Queue, reverter: new ChangeJournalReverter(factory, journal));
    }
}
