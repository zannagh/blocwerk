// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A source database with the real <see cref="ChangeJournalInterceptor"/>, a whole wall update run through the real
/// <see cref="WallBigUpdateService.PromoteAsync"/> (with a touch-up in between), and fresh replay targets. Isolated
/// SQLite databases, one connection per context (a kept-open connection only holds each in-memory database alive).
/// </summary>
public sealed class ChangeJournalReplayFixture : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly string cs;
    private readonly ICurrentUserService currentUser;
    private readonly List<SqliteConnection> targets = [];

    public ChangeJournalReplayFixture()
    {
        cs = TestDbContextFactory.IsolatedDatabase();
        connection = new SqliteConnection(cs);
        connection.Open();
        Journal = new ChangeJournal(CreateContext);
        Factory = new JournallingDbContextFactory(cs, Journal);
        using (var db = CreateContext())
        {
            db.Database.EnsureCreated();
        }

        currentUser = Substitute.For<ICurrentUserService>();
        currentUser.GetCurrentUserAsync().Returns(_ => Task.FromResult(Owner));
    }

    public User Owner { get; } = new() { Identifier = "owner@test", DisplayName = "Owner" };

    public ChangeJournal Journal { get; }

    public IDbContextFactory<BlocwerkDbContext> Factory { get; }

    public string ConnectionString => cs;

    public void Dispose()
    {
        foreach (var t in targets)
        {
            t.Dispose();
        }

        connection.Dispose();
    }

    public async Task<ChangeJournalReplayScenario> RunUpdateAsync(TouchupMode touchup, bool addedIsTwin = false)
    {
        var s = new ChangeJournalReplayScenario { WallId = Guid.NewGuid(), OldHoldId = Guid.NewGuid(), BoulderId = Guid.NewGuid() };
        await SeedBaseAsync(Factory, s);
        await Task.Delay(20);
        await StageAsync(s);

        await Task.Delay(20);
        if (touchup == TouchupMode.Adhoc)
        {
            await AdhocTouchupAsync(s);
        }
        else if (touchup == TouchupMode.Service)
        {
            await ServiceTouchupAsync(s);
        }
        else if (touchup == TouchupMode.Session)
        {
            await SessionTouchupAsync(s);
        }

        await Task.Delay(20);
        var accepted = touchup != TouchupMode.None && !addedIsTwin ? new List<Guid> { s.AddedId } : [];
        var twin = addedIsTwin ? s.AddedId : s.TwinId;
        await BigUpdateService().PromoteAsync(
            s.WallId,
            new BigUpdateConfirmation([new CarryoverDecision(s.OldHoldId, CarryKind.Carried, twin)], accepted, [], []));
        return s;
    }

    public async Task<ChangeJournalReplayReport> ReplayAsync(
        ChangeJournalReplayScenario s, ChangeJournalPackage package, bool dryRun, bool freshTarget = false, bool force = false)
    {
        if (s.TargetFactory is null || freshTarget)
        {
            await NewTargetAsync(s);
        }

        return await new ChangeJournalReplayer(s.TargetFactory!, s.TargetJournal!).ImportReplayAsync(package, dryRun, force);
    }

    public Task<ChangeJournalPackage> ExportAsync(IEnumerable<Guid> batchIds) =>
        new ChangeJournalExporter(Factory).ExportBatchesAsync(batchIds);

    /// <summary>Every batch recorded after the seed: the wall-scoped ones and the unscoped adhoc ones.</summary>
    public async Task<List<Guid>> AllBatchIdsAsync()
    {
        await using var db = CreateContext();
        var seed = await db.ChangeJournalBatches.Where(b => b.ScopeId == null).OrderBy(b => b.CreatedAt).Select(b => b.Id).FirstAsync();
        var all = await db.ChangeJournalBatches.Select(b => b.Id).ToListAsync();
        return all.Where(id => id != seed).ToList();
    }

    public async Task<Guid> WallUpdateBatchIdAsync(Guid wallId)
    {
        await using var db = CreateContext();
        return await db.ChangeJournalBatches
            .Where(b => b.ScopeId == wallId && b.Label == ChangeJournal.WallUpdateBatchLabel)
            .Select(b => b.Id)
            .SingleAsync();
    }

    public BlocwerkDbContext CreateContext() => Factory.CreateDbContext();

    public WallPanelService PanelService() =>
        new(
            Factory,
            currentUser,
            Substitute.For<IHoldDetectionService>(),
            Substitute.For<IHoldOverlapMatcher>(),
            NullLogger<WallPanelService>.Instance,
            changeJournal: Journal);

    private WallBigUpdateService BigUpdateService() =>
        new(
            Factory,
            currentUser,
            Substitute.For<IHoldDetectionService>(),
            Substitute.For<IHoldOverlapMatcher>(),
            NullLogger<WallBigUpdateService>.Instance,
            Journal);

    /// <summary>StageAsync's writes: panel + three detections + the twin + a detection the admin later deletes, one batch.</summary>
    private async Task StageAsync(ChangeJournalReplayScenario s)
    {
        using var batch = Journal.BeginWallUpdateBatch(s.WallId);
        await using var db = CreateContext();
        var panel = new WallPanel
        {
            WallId = s.WallId, Col = 0, Row = 0, StagedPhoto = [4, 5, 6], StagedPhotoContentType = "image/jpeg", Generation = 1,
        };
        s.PanelId = panel.Id;
        db.WallPanels.Add(panel);
        for (var i = 0; i < 3; i++)
        {
            db.Holds.Add(Staged(s, Guid.NewGuid(), 0.2 + (i * 0.05)));
        }

        db.Holds.Add(Staged(s, s.TwinId, 0.5));
        db.Holds.Add(Staged(s, s.DeletedId, 0.8));
        await db.SaveChangesAsync();
    }

    private async Task AdhocTouchupAsync(ChangeJournalReplayScenario s)
    {
        await using var db = CreateContext();
        (await db.Holds.SingleAsync(h => h.Id == s.TwinId)).X = 0.6;
        await db.SaveChangesAsync();
        db.Holds.Remove(await db.Holds.SingleAsync(h => h.Id == s.DeletedId));
        await db.SaveChangesAsync();
        db.Holds.Add(Staged(s, s.AddedId, 0.9));
        await db.SaveChangesAsync();
    }

    private async Task ServiceTouchupAsync(ChangeJournalReplayScenario s)
    {
        var panels = PanelService();
        await panels.UpdateStagedHoldAsync(s.WallId, s.TwinId, 0.6, 0.5, 0.02);
        await panels.DeleteStagedHoldAsync(s.WallId, s.DeletedId);
        s.AddedId = await panels.AddStagedHoldAsync(s.WallId, s.PanelId, 0.9, 0.9, 0.02);
    }

    /// <summary>
    /// The session-scoped touch-up: the open session (and its "Changed onto the deleted hold" verdict) is written raw,
    /// as session rows are not journalled; the edits then go through the session-scoped service calls.
    /// </summary>
    private async Task SessionTouchupAsync(ChangeJournalReplayScenario s)
    {
        await using (var db = CreateContext())
        {
            var session = WallUpdateSessions.Open(db, s.WallId, stagedGeneration: 1, Owner.Id);
            s.SessionId = session.Id;
            db.WallUpdateHoldDecisions.Add(new WallUpdateHoldDecision
            {
                SessionId = session.Id, Kind = WallUpdateHoldDecisionKind.Carry, HoldId = s.OldHoldId,
                PairedHoldId = s.DeletedId, CarryKind = CarryKind.Changed, Confirmed = true,
            });
            await db.SaveChangesAsync();
        }

        var panels = PanelService();
        await panels.UpdateStagedHoldInSessionAsync(s.WallId, s.SessionId, s.TwinId, 0.6, 0.5, 0.02);
        s.ReleasedTwins = await panels.DeleteStagedHoldInSessionAsync(s.WallId, s.SessionId, s.DeletedId, StagedTwinDeletion.ReleaseTwin);
        s.AddedId = await panels.AddStagedHoldInSessionAsync(s.WallId, s.SessionId, s.PanelId, 0.9, 0.9, 0.02);
    }

    private static Hold Staged(ChangeJournalReplayScenario s, Guid id, double xy) => new()
    {
        Id = id, WallId = s.WallId, WallPanelId = s.PanelId, X = xy, Y = xy, Radius = 0.02, Generation = 1,
        IsAutoDetected = true, NeedsReview = true,
    };

    private async Task NewTargetAsync(ChangeJournalReplayScenario s)
    {
        var target = new SqliteConnection(TestDbContextFactory.IsolatedDatabase());
        target.Open();
        targets.Add(target);
        var targetJournal = new ChangeJournal();
        s.TargetJournal = targetJournal;
        s.TargetFactory = new JournallingDbContextFactory(target.ConnectionString, targetJournal);
        await using (var db = s.TargetFactory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
        }

        await SeedBaseAsync(s.TargetFactory, s);
    }

    private async Task SeedBaseAsync(IDbContextFactory<BlocwerkDbContext> f, ChangeJournalReplayScenario s)
    {
        await using var db = f.CreateDbContext();
        if (!await db.Users.AnyAsync(u => u.Id == Owner.Id))
        {
            db.Users.Add(new User { Id = Owner.Id, Identifier = Owner.Identifier, DisplayName = Owner.DisplayName });
        }

        db.Walls.Add(new Wall
        {
            Id = s.WallId, Name = "Test Wall", OwnerId = Owner.Id, CurrentGeneration = 0, Photo = [1, 2, 3], PhotoContentType = "image/jpeg",
        });
        db.WallMembers.Add(new WallMember { WallId = s.WallId, UserId = Owner.Id, Role = WallRole.Admin });
        db.Holds.Add(new Hold { Id = s.OldHoldId, WallId = s.WallId, X = 0.1, Y = 0.1, Radius = 0.02, Generation = 0 });
        db.Boulders.Add(new Boulder
        {
            Id = s.BoulderId, WallId = s.WallId, Name = "Boulder", CreatedByUserId = Owner.Id, Generation = 0,
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        });
        db.BoulderHolds.Add(new BoulderHold { BoulderId = s.BoulderId, HoldId = s.OldHoldId });
        await db.SaveChangesAsync();
    }
}
