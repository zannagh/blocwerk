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
/// The time-window export (<c>export?wallId=&amp;since=&amp;includeUnscoped=true</c>) must find an unscoped batch whose
/// entries are only HoldGenerationLink updates (the move re-measure written at Apply): such an entry carries neither the
/// wall's id nor a hold, panel or boulder key. Left out, a later batch that depends on it conflicts on replay.
/// </summary>
public sealed class ChangeJournalWindowExportTests : IDisposable
{
    private readonly ChangeJournalReplayFixture f = new();

    public void Dispose() => f.Dispose();

    [Fact]
    public async Task AWindow_OverPromote_AGenerationLinkOnlyRemeasure_AndALaterDelete_ReplaysInOnePackage_WithNoConflict()
    {
        var s = await f.RunUpdateAsync(TouchupMode.None);
        var wallUpdate = await f.WallUpdateBatchIdAsync(s.WallId);
        Guid link;
        DateTimeOffset since;
        await using (var db = f.CreateContext())
        {
            link = (await db.HoldGenerationLinks.FirstAsync(l => l.WallId == s.WallId)).Id;
            since = (await db.ChangeJournalBatches.SingleAsync(b => b.Id == wallUpdate)).CreatedAt;
        }

        // The re-measure: an unscoped save that only updates the link's move columns.
        await Task.Delay(20);
        var remeasure = await RemeasureAsync(link);

        // A later wall-scoped batch that deletes the link: its before-image is the re-measured row.
        await Task.Delay(20);
        using (f.Journal.BeginBatch("look-over", ChangeJournalScopeKind.Wall, s.WallId))
        {
            await using var db = f.CreateContext();
            db.HoldGenerationLinks.Remove(await db.HoldGenerationLinks.SingleAsync(l => l.Id == link));
            await db.SaveChangesAsync();
        }

        var package = await Service(await AdminAsync()).ExportWallAsync(s.WallId, since, null, includeUnscoped: true, default);

        Assert.Contains(package.Batches, b => b.Id == remeasure);
        var order = package.Entries.Where(e => e.KeyJson.Contains(link.ToString())).Select(e => e.Op).ToList();
        Assert.Equal([ChangeJournalOp.Insert, ChangeJournalOp.Update, ChangeJournalOp.Delete], order);

        var report = await f.ReplayAsync(s, package, dryRun: false);
        Assert.True(report.Applied);
        Assert.DoesNotContain(report.Rows, r => r.Outcome == ChangeJournalReplayOutcome.Conflict);
        await using var dst = s.TargetFactory!.CreateDbContext();
        Assert.False(await dst.HoldGenerationLinks.AnyAsync(l => l.Id == link));
    }

    private async Task<Guid> RemeasureAsync(Guid link)
    {
        await using var db = f.CreateContext();
        var row = await db.HoldGenerationLinks.SingleAsync(l => l.Id == link);
        row.MoveDistanceMm = 42.5;
        row.MoveRotationDeg = 3;
        await db.SaveChangesAsync();
        var entry = await db.ChangeJournalEntries
            .Where(e => e.EntityType == nameof(HoldGenerationLink) && e.Op == ChangeJournalOp.Update)
            .SingleAsync();
        Assert.DoesNotContain("WallId", entry.AfterJson);
        return entry.BatchId;
    }

    private ChangeJournalAdminService Service(User actingUser)
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
