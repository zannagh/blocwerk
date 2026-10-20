// <copyright file="WallEnhancementScenarios.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Enhancement;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests.Enhancement;

/// <summary>The enhancement scenarios both providers run (SQLite in <see cref="WallEnhancementServiceTests"/>, Postgres in <see cref="PostgresWallEnhancementTests"/>).</summary>
internal static class WallEnhancementScenarios
{
    /// <summary>
    /// Start → steps in wall-scoped batches → one item per hold and kind → keep / undo / confirm / merge → finalize → the
    /// generation refuses a second run → a new generation enables it again.
    /// </summary>
    public static async Task FullCycleAsync(WallEnhancementFixture f)
    {
        var (h1, h2, h3) = (await f.AddHoldAsync(0.2, 0.2), await f.AddHoldAsync(0.5, 0.5), await f.AddHoldAsync(0.8, 0.8));
        var service = f.Service(
            FakeEnhancementStep.Writing(f, "outlines", 100, WallEnhancementItemKind.Shape, [h1, h2], h =>
            {
                h.ShapePoints = ShapePoint.DefaultOctagon(0.05);
                h.Radius = 0.05;
            }),
            FakeEnhancementStep.Writing(f, "cleanup", 300, WallEnhancementItemKind.Shape, [h1], h => h.Radius = 0.04),
            FakeEnhancementStep.Writing(f, "colours", 400, WallEnhancementItemKind.Colour, [h3], h => (h.Color, h.ProposedColor) = ("red", "red")),
            FakeEnhancementStep.Suggesting("duplicates", 500, WallEnhancementItemKind.Duplicate, new WallEnhancementSuggestion(h1, h2, null, nameof(HoldMergeMode.KeepRight))));

        f.Duplicates.MergeAsync(f.WallId, h1, h2, HoldMergeMode.KeepRight, Arg.Any<CancellationToken>())
            .Returns(new HoldMergeResult(Guid.NewGuid(), h2, h1, 0, 0));
        var view = await f.StartAndWaitAsync(service);

        Assert.Equal(WallEnhancementState.Review, view.State);
        Assert.Equal([(WallEnhancementItemKind.Shape, 2), (WallEnhancementItemKind.Colour, 1), (WallEnhancementItemKind.Duplicate, 1)],
            view.Counts.Select(c => (c.Kind, c.Pending)).ToList());
        var batches = await f.BatchesAsync();
        foreach (var label in new[] { "wall-enhance:outlines", "wall-enhance:cleanup", "wall-enhance:colours" })
        {
            var batch = Assert.Single(batches, b => b.Label == label);
            Assert.Equal((ChangeJournalScopeKind.Wall, f.WallId, f.Harness.Owner.Id.ToString()), (batch.ScopeKind, batch.ScopeId, batch.Actor));
        }

        var items = await f.ItemsAsync();
        var undo = await service.DecideAsync(f.WallId, items.Single(i => i.Kind == WallEnhancementItemKind.Shape && i.HoldId == h1).Id, accept: false);
        Assert.Equal(WallEnhancementDecision.Rejected, undo.Decision);
        var restored = await f.GetHoldAsync(h1);
        Assert.Equal((0.03, null), (restored.Radius, restored.ShapePoints));
        Assert.Equal(0.05, (await f.GetHoldAsync(h2)).Radius);

        await service.DecideAsync(f.WallId, items.Single(i => i.Kind == WallEnhancementItemKind.Shape && i.HoldId == h2).Id, accept: true);
        await service.DecideAsync(f.WallId, items.Single(i => i.Kind == WallEnhancementItemKind.Colour).Id, accept: true);
        var confirmed = await f.GetHoldAsync(h3);
        Assert.Equal(("red", null), (confirmed.Color, confirmed.ProposedColor));
        await Assert.ThrowsAsync<UserFacingException>(() => service.FinalizeAsync(f.WallId));
        await service.DecideAsync(f.WallId, items.Single(i => i.Kind == WallEnhancementItemKind.Duplicate).Id, accept: true);
        await f.Duplicates.Received(1).MergeAsync(f.WallId, h1, h2, HoldMergeMode.KeepRight, Arg.Any<CancellationToken>());

        var finalized = await service.FinalizeAsync(f.WallId);

        Assert.Equal(WallEnhancementState.Finalized, finalized.State);
        Assert.NotNull(finalized.FinalizedAt);
        Assert.Contains("already enhanced", (await Assert.ThrowsAsync<UserFacingException>(() => service.StartAsync(f.WallId))).Message);
        var after = await f.BatchesAsync();
        Assert.All(after.Where(b => b.Label.Contains("wall-enhance")), b => Assert.Equal((ChangeJournalScopeKind.Wall, f.WallId), (b.ScopeKind, b.ScopeId)));
        Assert.Contains(after, b => b.Label == WallEnhancementKinds.UndoLabel(WallEnhancementItemKind.Shape));
        Assert.Contains(after, b => b.Label == WallEnhancementKinds.ReviewBatchLabel);

        await f.BumpGenerationAsync();
        var fresh = await service.GetAsync(f.WallId);
        Assert.Equal((WallEnhancementState.NotStarted, 1), (fresh.State, fresh.Generation));
        Assert.NotNull(fresh.PreviousFinalizedAt);
        Assert.Equal(WallEnhancementState.Review, (await f.StartAndWaitAsync(service)).State);
    }

    /// <summary>The database itself refuses a second run of the same wall generation.</summary>
    public static async Task UniqueIndexAsync(WallEnhancementFixture f)
    {
        await using (var db = f.Harness.CreateContext())
        {
            db.WallEnhancementRuns.Add(new WallEnhancementRun { WallId = f.WallId, Generation = 0, CreatedByUserId = f.Harness.Owner.Id });
            await db.SaveChangesAsync();
        }

        await using var second = f.Harness.CreateContext();
        second.WallEnhancementRuns.Add(new WallEnhancementRun { WallId = f.WallId, Generation = 0, CreatedByUserId = f.Harness.Owner.Id });
        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
    }

    /// <summary>
    /// A run a restart cut off in step 2 (step 1 recorded, step 2 started and half written): the resumer launches it, step 1
    /// does not run again, step 2 does, and the hold step 2 wrote twice is one review item.
    /// </summary>
    public static async Task ResumeAsync(WallEnhancementFixture f)
    {
        var (h1, h2) = (await f.AddHoldAsync(0.2, 0.2), await f.AddHoldAsync(0.6, 0.6));
        var first = FakeEnhancementStep.Writing(f, "first", 100, WallEnhancementItemKind.Shape, [h1], h => h.Radius = 0.05);
        var second = FakeEnhancementStep.Writing(f, "second", 200, WallEnhancementItemKind.Shape, [h2], h => h.Radius += 0.01);
        f.Service(first, second);
        var started = DateTimeOffset.UtcNow.AddMinutes(-1);
        WallEnhancementStepRecord[] records =
        [
            new("first", "Fake first", WallEnhancementStepOutcomeKind.Done, "done", started, started, []),
            new("second", "Fake second", WallEnhancementStepOutcomeKind.Running, string.Empty, started, null, []),
        ];
        await using (var db = f.Harness.CreateContext())
        {
            db.WallEnhancementRuns.Add(new WallEnhancementRun
            {
                WallId = f.WallId, Generation = 0, CreatedByUserId = f.Harness.Owner.Id, StepsJson = WallEnhancementStepRecord.Write(records), CurrentStep = "second",
            });
            await db.SaveChangesAsync();
        }

        await HalfWriteAsync(f, h2);
        var resumer = new WallEnhancementResumer(f.Journalling, f.Runner!, Microsoft.Extensions.Logging.Abstractions.NullLogger<WallEnhancementResumer>.Instance);

        Assert.Equal(1, await resumer.ResumeInterruptedAsync(CancellationToken.None));
        await f.Runner!.WhenIdleAsync((await f.RunAsync()).Id);

        var run = await f.RunAsync();
        Assert.Equal(WallEnhancementStatus.Review, run.Status);
        Assert.Equal((0, 1), (first.Runs, second.Runs));
        var item = Assert.Single(await f.ItemsAsync());
        Assert.Equal(h2, item.HoldId);
        Assert.Equal(2, WallEnhancementStepRecord.Parse(run.StepsJson).Single(r => r.Key == "second").BatchIds.Count);
        Assert.Equal(0.05, (await f.GetHoldAsync(h2)).Radius, 6);
    }

    /// <summary>What the cut-off step had written before the restart: one save in its own batch.</summary>
    private static async Task HalfWriteAsync(WallEnhancementFixture f, Guid holdId)
    {
        using (f.Journal.BeginBatch(WallEnhancementKinds.BatchLabel("second"), ChangeJournalScopeKind.Wall, f.WallId))
        {
            await using var db = await f.Journalling.CreateDbContextAsync();
            db.CurrentUserId = f.Harness.Owner.Id;
            (await db.Holds.FirstAsync(h => h.Id == holdId)).Radius += 0.01;
            await db.SaveChangesAsync();
        }
    }
}
