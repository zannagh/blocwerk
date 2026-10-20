// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A merge and a split are replayable: the wall's time-window export carries both batches, and replaying it onto a
/// copy of the wall as it was before gives the same holds, outlines and memberships, with no conflict.
/// </summary>
public sealed class HoldToolReplayTests : IDisposable
{
    private readonly ChangeJournalReplayFixture f = new();

    public void Dispose() => f.Dispose();

    [Fact]
    public async Task AMergeAndASplit_ExportedByTheWindow_ReplayOntoThePristineWall_WithNoConflict()
    {
        var s = await f.RunUpdateAsync(TouchupMode.None);
        var wallUpdate = await f.WallUpdateBatchIdAsync(s.WallId);
        DateTimeOffset since;
        List<Hold> live;
        await using (var db = f.CreateContext())
        {
            since = (await db.ChangeJournalBatches.SingleAsync(b => b.Id == wallUpdate)).CreatedAt;
            var generation = await db.Walls.Where(w => w.Id == s.WallId).Select(w => w.CurrentGeneration).SingleAsync();
            live = [.. (await db.Holds.Where(h => h.WallId == s.WallId && h.WallPanelId != null && h.Generation == generation && !h.IsVirtual).ToListAsync()).OrderBy(h => h.X)];
        }

        // Three holds of the live photo to work on, added in a wall-scoped batch of their own (replayed too).
        Assert.NotEmpty(live);
        var (first, second, third) = (NewHold(live[0], 0.60), NewHold(live[0], 0.63), NewHold(live[0], 0.80));
        await Task.Delay(20);
        using (f.Journal.BeginBatch("setup", ChangeJournalScopeKind.Wall, s.WallId))
        {
            await using var db = f.CreateContext();
            db.Holds.AddRange(first, second, third);
            await db.SaveChangesAsync();
        }

        var tools = Tools();
        await Task.Delay(20);
        var merge = await tools.MergeAsync(s.WallId, first.Id, second.Id);
        await Task.Delay(20);
        var cut = third;
        var split = await tools.SplitAsync(s.WallId, cut.Id, [new NormalizedPoint(cut.X + 0.005, cut.Y - 0.1), new NormalizedPoint(cut.X + 0.005, cut.Y + 0.1)], []);

        var package = await Admin(await AdminAsync()).ExportWallAsync(s.WallId, since, null, includeUnscoped: true, default);
        Assert.Contains(package.Batches, b => b.Id == merge.BatchId);
        Assert.Contains(package.Batches, b => b.Id == split.BatchId);

        var report = await f.ReplayAsync(s, package, dryRun: false);

        Assert.True(report.Applied);
        Assert.DoesNotContain(report.Rows, r => r.Outcome == ChangeJournalReplayOutcome.Conflict);
        await using var src = f.CreateContext();
        await using var dst = s.TargetFactory!.CreateDbContext();
        var expected = await src.Holds.AsNoTracking().Where(h => h.WallId == s.WallId).OrderBy(h => h.Id).Select(h => new { h.Id, h.X, h.Y, h.Radius }).ToListAsync();
        var actual = await dst.Holds.AsNoTracking().Where(h => h.WallId == s.WallId).OrderBy(h => h.Id).Select(h => new { h.Id, h.X, h.Y, h.Radius }).ToListAsync();
        Assert.Equal(expected, actual);
        Assert.False(await dst.Holds.AnyAsync(h => h.Id == second.Id));
        Assert.Equal(
            (await src.Holds.AsNoTracking().SingleAsync(h => h.Id == split.OtherHoldId)).ShapePoints!.Count,
            (await dst.Holds.AsNoTracking().SingleAsync(h => h.Id == split.OtherHoldId)).ShapePoints!.Count);
    }

    private static Hold NewHold(Hold onPhoto, double x) => new()
    {
        WallId = onPhoto.WallId, WallPanelId = onPhoto.WallPanelId, Generation = onPhoto.Generation, X = x, Y = 0.6, Radius = 0.02, Color = "red",
    };

    private HoldMergeSplitService Tools()
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.GetCurrentUserAsync().Returns(_ => Task.FromResult(f.Owner));
        return new HoldMergeSplitService(
            f.Factory, currentUser, f.Journal, new ChangeJournalReverter(f.Factory, f.Journal), NullLogger<HoldMergeSplitService>.Instance);
    }

    private ChangeJournalAdminService Admin(User actingUser)
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.GetCurrentUserAsync().Returns(_ => Task.FromResult(actingUser));
        return new ChangeJournalAdminService(
            new TestRootDbContextFactory(f.ConnectionString),
            currentUser,
            new ChangeJournalExporter(f.Factory),
            new ChangeJournalReplayer(f.Factory, f.Journal),
            new ChangeJournalReverter(f.Factory, f.Journal),
            NullLogger<ChangeJournalAdminService>.Instance);
    }

    private async Task<User> AdminAsync()
    {
        var admin = new User { Identifier = "admin@test", DisplayName = "Admin", Role = IdentityRole.Admin };
        await using var db = f.CreateContext();
        db.Users.Add(admin);
        await db.SaveChangesAsync();
        return admin;
    }
}
