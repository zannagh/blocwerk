// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.Replay;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A package that carries the source's computed results: the target takes over the finished view, the volumes, the proposals
/// and every matching hold's 3D fields, finishes nothing and queues nothing; only holds that differ here are computed again.
/// Panel truth (images, positions, outlines, links, boulders, review state) never changes.
/// </summary>
public class CapturePackageResultsTests
{
    [Fact]
    public Task MatchingHolds_TakeTheCarriedResults_AndNothingIsComputedOrQueued() => MatchingAsync(new WallTestHarness());

    [PostgresFact]
    [Trait("Db", "Postgres")]
    public Task MatchingHolds_TakeTheCarriedResults_OnPostgres() => MatchingAsync(new WallTestHarness(PostgresTestDatabase.Create()));

    [Fact]
    public Task AHoldChangedOrAddedHere_KeepsItsFields_IsComputedAgain_AndDropsTheProposalItCovers() => UnmatchedAsync(new WallTestHarness());

    [PostgresFact]
    [Trait("Db", "Postgres")]
    public Task AHoldChangedOrAddedHere_IsComputedAgain_OnPostgres() => UnmatchedAsync(new WallTestHarness(PostgresTestDatabase.Create()));

    private static async Task MatchingAsync(WallTestHarness harness)
    {
        using var h = harness;
        using var f = await CapturePackageFlow.FinishedAsync(h);
        var seed = await CapturePackageResultsSeed.CreateAsync(h);
        var (manifest, bytes) = await f.ExportAsync();
        await CapturePackageResultsSeed.ForgetAsync(f, manifest);
        var truth = await CapturePackageResultsSeed.PanelTruthAsync(h);
        f.Runner.Scenario.SplatClient.IsConfigured = false;

        await QueuedAsync(f);
        var report = await f.Service.BeginImportAsync(manifest, CancellationToken.None);
        await f.UploadAllAsync(manifest, bytes);
        var committed = await f.Service.CommitImportAsync(f.CaptureId, CancellationToken.None);

        Assert.True(report.CanCommit, string.Join(" | ", report.Blockers));
        Assert.True(committed.Committed, string.Join(" | ", committed.Blockers));
        Assert.Equal(truth, await CapturePackageResultsSeed.PanelTruthAsync(h));
        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureStatus.Succeeded, capture.Status);
        Assert.Equal(manifest.Rows.Results!.FollowUpJson, capture.FollowUpJson);
        Assert.NotNull((await db.GpuJobs.SingleAsync()).InstalledAt);
        Assert.Equal(manifest.Rows.Results.Splat.Id, (await db.WallGeometrySplats.SingleAsync()).Id);
        Assert.Single(await db.WallVolumes.ToListAsync());
        Assert.Equal(2, await db.HoldProposals.CountAsync());
        await AssertCarriedAsync(db, manifest, seed.HoldIds);
        var run = await db.HoldPlacementRuns.SingleAsync(r => r.Trigger == HoldPlacementTrigger.Import);
        Assert.Equal(3, run.PlacedCount);

        f.Refinements.DidNotReceiveWithAnyArgs().Enqueue(default, default!);
        Assert.Equal(1, f.Runner.Finishes());
        Assert.Empty(await QueuedAsync(f));
    }

    private static async Task UnmatchedAsync(WallTestHarness harness)
    {
        using var h = harness;
        using var f = await CapturePackageFlow.FinishedAsync(h);
        var seed = await CapturePackageResultsSeed.CreateAsync(h);
        var (manifest, bytes) = await f.ExportAsync();
        await CapturePackageResultsSeed.ForgetAsync(f, manifest);
        var moved = seed.HoldIds[1];
        var added = new Hold { WallId = h.WallId, WallPanelId = seed.PanelId, X = 0.7, Y = 0.2, Radius = 0.03 };
        await using (var db = h.CreateContext())
        {
            // Moved here onto the spot of the first proposal, with its own (stale) 3D fields; one hold drawn here only.
            await db.Holds.Where(x => x.Id == moved).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.X, CapturePackageResultsSeed.FreeX).SetProperty(x => x.Y, CapturePackageResultsSeed.FreeY)
                .SetProperty(x => x.FacetId, "old").SetProperty(x => x.PlaneAMm, 1));
            db.Holds.Add(added);
            await db.SaveChangesAsync();
        }

        var truth = await CapturePackageResultsSeed.PanelTruthAsync(h);
        await QueuedAsync(f);
        await f.Service.BeginImportAsync(manifest, CancellationToken.None);
        await f.UploadAllAsync(manifest, bytes);
        Assert.True((await f.Service.CommitImportAsync(f.CaptureId, CancellationToken.None)).Committed);

        Assert.Equal(truth, await CapturePackageResultsSeed.PanelTruthAsync(h));
        await using (var db = h.CreateContext())
        {
            var kept = await db.Holds.SingleAsync(x => x.Id == moved);
            Assert.Equal(("old", 1d), (kept.FacetId, kept.PlaneAMm!.Value));
            await AssertCarriedAsync(db, manifest, [seed.HoldIds[0], seed.HoldIds[2]]);
            Assert.Equal([seed.FarProposalId], await db.HoldProposals.Select(p => p.Id).ToListAsync());
        }

        f.Refinements.Received(1).Enqueue(h.WallId, Arg.Is<IEnumerable<Guid>>(ids => ids.Order().SequenceEqual(new[] { moved, added.Id }.Order())));
        Assert.Empty(await QueuedAsync(f));
    }

    [Fact]
    public async Task AFormatOnePackage_StillNeedsTheSplatWorker_AndIsQueuedToFinishItsView()
    {
        using var h = new WallTestHarness();
        using var f = await CapturePackageFlow.FinishedAsync(h);
        await CapturePackageResultsSeed.CreateAsync(h);
        var (exported, bytes) = await f.ExportAsync();
        await CapturePackageResultsSeed.ForgetAsync(f, exported);
        var manifest = CapturePackageFlow.WithoutResults(exported);
        await QueuedAsync(f);

        await f.Service.BeginImportAsync(manifest, CancellationToken.None);
        await f.UploadAllAsync(manifest, bytes);
        Assert.True((await f.Service.CommitImportAsync(f.CaptureId, CancellationToken.None)).Committed);

        await using var db = h.CreateContext();
        Assert.Equal(WallCaptureStatus.Splatting, (await db.WallCaptures.SingleAsync()).Status);
        Assert.Empty(await db.WallVolumes.ToListAsync());
        Assert.Empty(await db.HoldProposals.ToListAsync());
        Assert.All(await db.Holds.ToListAsync(), x => Assert.Null(x.FacetId));
        Assert.False(await db.HoldPlacementRuns.AnyAsync(r => r.Trigger == HoldPlacementTrigger.Import));
        f.Refinements.DidNotReceiveWithAnyArgs().Enqueue(default, default!);
        Assert.Equal([f.CaptureId], await QueuedAsync(f));
    }

    [Fact]
    public async Task ResultsAreNotCarried_WhenTheModelIsNotActiveAnyMore()
    {
        using var h = new WallTestHarness();
        using var f = await CapturePackageFlow.FinishedAsync(h);
        await using (var db = h.CreateContext())
        {
            await db.WallGeometryModels.ExecuteUpdateAsync(s => s.SetProperty(m => m.IsActive, false));
        }

        var (manifest, _) = await f.ExportAsync();

        Assert.Null(manifest.Rows.Results);
        Assert.DoesNotContain(manifest.Files, x => x.Role == "photo-real-view");
        Assert.Contains(manifest.Warnings, w => w.Contains("not carried"));
    }

    private static async Task AssertCarriedAsync(BlocwerkDbContext db, CapturePackageManifest manifest, IEnumerable<Guid> ids)
    {
        foreach (var id in ids)
        {
            var hold = await db.Holds.AsNoTracking().SingleAsync(x => x.Id == id);
            Assert.Equal(manifest.Rows.Results!.Holds.Single(r => r.HoldId == id) with { Geometry = string.Empty }, CapturePackageHoldResult.Of(hold) with { Geometry = string.Empty });
            Assert.NotNull(hold.FootprintMm);
        }
    }

    /// <summary>Takes what the capture queue holds now (the flow's own start leaves its capture queued once).</summary>
    private static async Task<List<Guid>> QueuedAsync(CapturePackageFlow f)
    {
        var queued = new List<Guid>();
        try
        {
            while (true)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                queued.Add(await f.Runner.Scenario.Queue.DequeueAsync(cts.Token));
            }
        }
        catch (OperationCanceledException)
        {
            return queued;
        }
    }
}
