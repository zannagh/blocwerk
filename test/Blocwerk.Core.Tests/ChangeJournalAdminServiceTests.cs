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
/// <see cref="ChangeJournalAdminService"/>: app administrators only, schema parity never forceable, dry runs write nothing,
/// and the listing finds the wall's batches (and, on request, the unscoped ones that touched it).
/// </summary>
public sealed class ChangeJournalAdminServiceTests : IDisposable
{
    private readonly ChangeJournalReplayFixture f = new();

    public void Dispose() => f.Dispose();

    [Fact]
    public async Task ANonAdmin_IsRefusedEveryCall_AndNothingIsWritten()
    {
        var s = await f.RunUpdateAsync(TouchupMode.Service);
        var batchId = await f.WallUpdateBatchIdAsync(s.WallId);
        var service = Service(f.Owner);
        var package = await f.ExportAsync([batchId]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListBatchesAsync(s.WallId, false, null, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ExportAsync([batchId], default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReplayAsync(package, false, true, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RevertAsync(batchId, false, true, default));

        await using var db = f.CreateContext();
        Assert.Equal(ChangeJournalStatus.Recorded, (await db.ChangeJournalBatches.SingleAsync(b => b.Id == batchId)).Status);
    }

    [Fact]
    public async Task TheListing_HasTheWallUpdateBatchWithItsEntryCount_AndTheAdhocOnesOnRequest()
    {
        var s = await f.RunUpdateAsync(TouchupMode.Adhoc);
        var wallUpdate = await f.WallUpdateBatchIdAsync(s.WallId);
        var service = Service(await AdminAsync());
        int entries;
        await using (var db = f.CreateContext())
        {
            entries = await db.ChangeJournalEntries.CountAsync(e => e.BatchId == wallUpdate);
        }

        var scoped = await service.ListBatchesAsync(s.WallId, false, null, default);
        var all = await service.ListBatchesAsync(s.WallId, true, null, default);

        var batch = Assert.Single(scoped);
        Assert.Equal(wallUpdate, batch.Id);
        Assert.Equal(entries, batch.EntryCount);
        Assert.NotNull(batch.SealedAt);

        // The touch-up's three adhoc batches: a move (found by the hold's key), a delete and an insert (by WallId).
        Assert.True(all.Count(b => b.Label == "adhoc") >= 3);
        Assert.Equal(all.OrderBy(b => b.CreatedAt).Select(b => b.Id), all.Select(b => b.Id));
    }

    [Fact]
    public async Task AReplay_WithAnotherSchema_IsRefusedEvenWhenForced()
    {
        var s = await f.RunUpdateAsync(TouchupMode.None);
        var package = await f.ExportAsync([await f.WallUpdateBatchIdAsync(s.WallId)]);
        package.MigrationId = "20990101000000_FromTheFuture";

        var report = await Service(await AdminAsync()).ReplayAsync(package, dryRun: false, force: true, default);

        Assert.False(report.Applied);
        Assert.Contains("Schema parity mismatch", Assert.Single(report.FatalErrors));
    }

    [Fact]
    public async Task ARevertDryRun_ReportsWithoutWriting_AndTheRealRevertAgreesWithIt()
    {
        var s = await f.RunUpdateAsync(TouchupMode.Service);
        var batchId = await f.WallUpdateBatchIdAsync(s.WallId);
        var service = Service(await AdminAsync());
        var before = await HoldIdsAsync(s.WallId);

        var dry = await service.RevertAsync(batchId, dryRun: true, force: false, default);

        Assert.True(dry.DryRun);
        Assert.False(dry.Reverted);
        Assert.Null(dry.RevertBatchId);
        Assert.Equal(before, await HoldIdsAsync(s.WallId));
        await using (var db = f.CreateContext())
        {
            Assert.Equal(ChangeJournalStatus.Recorded, (await db.ChangeJournalBatches.SingleAsync(b => b.Id == batchId)).Status);
        }

        var real = await service.RevertAsync(batchId, dryRun: false, force: false, default);
        Assert.Equal(dry.Conflicts.Count == 0, real.Reverted);
        Assert.Equal(dry.Conflicts.Select(c => c.KeyJson).Order(), real.Conflicts.Select(c => c.KeyJson).Order());
    }

    [Fact]
    public async Task TheGzipPackage_RoundTrips_AndPlainJsonIsReadToo()
    {
        var s = await f.RunUpdateAsync(TouchupMode.Service);
        var package = await Service(await AdminAsync()).ExportAsync([await f.WallUpdateBatchIdAsync(s.WallId)], default);

        using var gz = new MemoryStream();
        await ChangeJournalPackageJson.WriteGzipAsync(package, gz, default);
        gz.Position = 0;
        var fromGzip = await ChangeJournalPackageJson.ReadAsync(gz, default);
        using var plain = new MemoryStream(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(package, ChangeJournalPackageJson.Options));
        var fromPlain = await ChangeJournalPackageJson.ReadAsync(plain, default);

        Assert.NotEmpty(package.Blobs);
        foreach (var read in new[] { fromGzip, fromPlain })
        {
            Assert.Equal(package.MigrationId, read.MigrationId);
            Assert.Equal(package.Entries.Select(e => (e.BatchId, e.Seq, e.Op, e.KeyJson)), read.Entries.Select(e => (e.BatchId, e.Seq, e.Op, e.KeyJson)));
            Assert.Equal(package.Blobs.Select(b => b.Sha256), read.Blobs.Select(b => b.Sha256));
        }
    }

    [Fact]
    public async Task AWholeUpdateExport_CarriesItsAdhocTouchups_LeavesOutLaterBatches_AndReplaysUnderTheWall()
    {
        var s = await f.RunUpdateAsync(TouchupMode.Adhoc);
        var wallUpdate = await f.WallUpdateBatchIdAsync(s.WallId);
        await Task.Delay(20);
        using (f.Journal.BeginBatch("look-over", ChangeJournalScopeKind.Wall, s.WallId))
        {
            await using var db = f.CreateContext();
            (await db.Holds.SingleAsync(h => h.Id == s.TwinId)).Name = "later";
            await db.SaveChangesAsync();
        }

        var service = Service(await AdminAsync());
        var listed = await service.ListBatchesAsync(s.WallId, includeUnscoped: true, since: null, default);
        var package = await service.ExportWallUpdateAsync(wallUpdate, default);

        // The listing groups the adhoc touch-ups under the update; the look-over came after its seal.
        Assert.All(listed.Where(b => b.Label == "adhoc" && b.CreatedAt > listed.Single(x => x.Id == wallUpdate).CreatedAt),
            b => Assert.Equal(wallUpdate, b.WallUpdateBatchId));
        Assert.Null(listed.Single(b => b.Label == "look-over").WallUpdateBatchId);

        Assert.NotNull(package);
        Assert.Contains(package.Batches, b => b.Id == wallUpdate);
        Assert.Equal(3, package.Batches.Count(b => b.Label == "adhoc"));
        Assert.DoesNotContain(package.Batches, b => b.Label == "look-over");

        var report = await f.ReplayAsync(s, package, dryRun: false);
        Assert.True(report.Applied);
        await using var dst = s.TargetFactory!.CreateDbContext();
        var replay = await dst.ChangeJournalBatches.SingleAsync(b => b.Id == report.ReplayBatchId);
        Assert.Equal((ChangeJournalScopeKind.Wall, (Guid?)s.WallId), (replay.ScopeKind, replay.ScopeId));
        Assert.Equal(0.6, (await dst.Holds.SingleAsync(h => h.Id == s.TwinId)).X);
    }

    [Fact]
    public async Task AWholeUpdateExport_OfABatchThatIsNoWallUpdate_IsNull()
    {
        var s = await f.RunUpdateAsync(TouchupMode.Adhoc);
        var service = Service(await AdminAsync());
        var notAnUpdate = (await service.ListBatchesAsync(s.WallId, true, null, default)).First(b => b.Label == "adhoc").Id;

        Assert.Null(await service.ExportWallUpdateAsync(notAnUpdate, default));
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

    private async Task<List<Guid>> HoldIdsAsync(Guid wallId)
    {
        await using var db = f.CreateContext();
        return await db.Holds.Where(h => h.WallId == wallId).OrderBy(h => h.Id).Select(h => h.Id).ToListAsync();
    }
}
