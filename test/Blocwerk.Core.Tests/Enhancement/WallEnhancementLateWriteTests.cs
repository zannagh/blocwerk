// <copyright file="WallEnhancementLateWriteTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enhancement;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests.Enhancement;

/// <summary>Writes a stopped step makes after its stop was recorded, and two stops of one run salvaging at once.</summary>
public sealed class WallEnhancementLateWriteTests
{
    [Fact]
    public async Task WritesAStoppedStepMakesAfterItsStopWasRecorded_AreUndoneByDiscardToo()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        f.StopGrace = TimeSpan.FromMilliseconds(100);
        var (early, late) = (await f.AddHoldAsync(0.2, 0.2), await f.AddHoldAsync(0.7, 0.7));
        var release = new TaskCompletionSource();
        var step = new FakeEnhancementStep("outlines", 100, WallEnhancementItemKind.Shape, async (ctx, _) =>
        {
            await WriteInOwnBatchAsync(f, ctx.UserId, early);
            await release.Task;
            await WriteInOwnBatchAsync(f, ctx.UserId, late);
            return WallEnhancementStepOutcome.Done("late");
        });
        var service = f.Service(step);
        await service.StartAsync(f.WallId);
        await WaitUntilAsync(async () => (await f.GetHoldAsync(early)).Radius > 0.04);

        await service.CancelAsync(f.WallId);
        Assert.Equal(early, Assert.Single(await f.ItemsAsync()).HoldId);
        release.SetResult();
        await f.Runner!.WhenIdleAsync((await f.RunAsync()).Id);

        Assert.Equal(2, (await f.ItemsAsync()).Count);
        Assert.Equal(2, WallEnhancementStepRecord.Parse((await f.RunAsync()).StepsJson).Single().BatchIds.Count);
        await service.DiscardAsync(f.WallId);
        Assert.Equal((0.03, 0.03), ((await f.GetHoldAsync(early)).Radius, (await f.GetHoldAsync(late)).Radius));
    }

    [Fact]
    public async Task TwoStopsSalvagingAtOnce_ListAHoldOnce()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        await RaceAsync(f);
    }

    /// <summary>Two contexts salvage the same step at once; the unique index keeps one item per run, kind and hold.</summary>
    internal static async Task RaceAsync(WallEnhancementFixture f)
    {
        var hold = await f.AddHoldAsync(0.3, 0.3);
        var runId = await AddRunningRunAsync(f);
        await WriteInOwnBatchAsync(f, f.Harness.Owner.Id, hold);

        for (var round = 0; round < 3; round++)
        {
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            {
                await using var db = await f.Journalling.CreateDbContextAsync();
                var run = await db.WallEnhancementRuns.FirstAsync(r => r.Id == runId);
                await WallEnhancementSalvage.SalvageStepAsync(db, run, "outlines", CancellationToken.None);
            })));
        }

        Assert.Single(await f.ItemsAsync());
        await using var check = f.Harness.CreateContext();
        check.WallEnhancementItems.Add(new WallEnhancementItem { RunId = runId, Kind = WallEnhancementItemKind.Shape, HoldId = hold });
        await Assert.ThrowsAsync<DbUpdateException>(() => check.SaveChangesAsync());
    }

    private static async Task<Guid> AddRunningRunAsync(WallEnhancementFixture f)
    {
        WallEnhancementStepRecord[] records =
        [
            new("outlines", "Outlines", WallEnhancementStepOutcomeKind.Running, string.Empty, DateTimeOffset.UtcNow.AddMinutes(-1), null, [],
                Kind: WallEnhancementItemKind.Shape, Labels: [WallEnhancementKinds.BatchLabel("outlines")]),
        ];
        await using var db = f.Harness.CreateContext();
        var run = new WallEnhancementRun
        {
            WallId = f.WallId, Generation = 0, CreatedByUserId = f.Harness.Owner.Id, StepsJson = WallEnhancementStepRecord.Write(records), CurrentStep = "outlines",
        };
        db.WallEnhancementRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    /// <summary>One write in a batch of its own under the step's label, as a step's service opens one.</summary>
    private static async Task WriteInOwnBatchAsync(WallEnhancementFixture f, Guid userId, Guid hold)
    {
        using (f.Journal.BeginBatch(WallEnhancementKinds.BatchLabel("outlines"), ChangeJournalScopeKind.Wall, f.WallId))
        {
            await using var db = await f.Journalling.CreateDbContextAsync();
            db.CurrentUserId = userId;
            (await db.Holds.FirstAsync(h => h.Id == hold)).Radius = 0.05;
            await db.SaveChangesAsync();
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 250 && !await condition(); i++)
        {
            await Task.Delay(20);
        }
    }
}
