// <copyright file="ApiWriteAuditTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests.Automation;

/// <summary>
/// The audit row of an API write exists before the write runs, is completed when it succeeded, goes away when it was
/// refused or threw (kept as Failed when it journalled rows), and a failed completion never fails a write that happened.
/// </summary>
public class ApiWriteAuditTests
{
    [Fact]
    public async Task TheAuditRow_IsWrittenBeforeTheWrite_AndCompletedAfterIt()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var audit = AutomationApiFixture.Audit(h, AutomationApiFixture.Journal(h));
        ChangeJournalStatus? during = null;

        var result = await audit.RunAsync(ApiKeys.Personal(), h.WallId, "test.write", async () =>
        {
            during = Assert.Single(await AutomationApiFixture.ApiBatchesAsync(h)).Status;
            return new OkResult();
        });

        Assert.IsType<OkResult>(result);
        Assert.Equal(ChangeJournalStatus.Pending, during);
        var batch = Assert.Single(await AutomationApiFixture.ApiBatchesAsync(h));
        Assert.Equal((ChangeJournalStatus.Recorded, h.Owner.Id.ToString()), (batch.Status, batch.Actor));
        Assert.NotNull(batch.SealedAt);
    }

    [Fact]
    public async Task ARefusedOrThrowingWrite_LeavesNoAuditRow()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var audit = AutomationApiFixture.Audit(h, AutomationApiFixture.Journal(h));

        await audit.RunAsync(ApiKeys.Personal(), h.WallId, "test.refused", () => Task.FromResult<IActionResult>(new ConflictResult()));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            audit.RunAsync(ApiKeys.Personal(), h.WallId, "test.throws", () => Task.FromException<IActionResult>(new InvalidOperationException())));

        Assert.Empty(await AutomationApiFixture.ApiBatchesAsync(h));
    }

    [Fact]
    public async Task AFailedWriteThatJournalledRows_IsKeptAsFailed_WithThoseRows()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var journal = AutomationApiFixture.Journal(h);
        var audit = AutomationApiFixture.Audit(h, journal);

        await audit.RunAsync(ApiKeys.Personal(), h.WallId, "test.partial", async () =>
        {
            await using var db = new JournalledDbContextFactory(h, journal).CreateDbContext();
            (await db.Walls.SingleAsync(w => w.Id == h.WallId)).Name = "Renamed";
            await db.SaveChangesAsync();
            return new ConflictResult();
        });

        var batch = Assert.Single(await AutomationApiFixture.ApiBatchesAsync(h));
        Assert.Equal(ChangeJournalStatus.Failed, batch.Status);
        await using var check = h.CreateContext();
        Assert.Equal("Wall", (await check.ChangeJournalEntries.SingleAsync(e => e.BatchId == batch.Id)).EntityType);
    }

    [Fact]
    public async Task AnAuditThatCannotBeCompleted_StillAnswersTheWriteThatHappened()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var calls = 0;
        var journal = new ChangeJournal(() => ++calls == 1
            ? h.RootContextFactory.CreateDbContext()
            : throw new InvalidOperationException("The journal is unavailable."));
        var audit = new ApiWriteAudit(journal, h.CurrentUser, NullLogger<ApiWriteAudit>.Instance);

        var result = await audit.RunAsync(ApiKeys.Personal(), h.WallId, "test.write", () => Task.FromResult<IActionResult>(new NoContentResult()));

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(ChangeJournalStatus.Pending, Assert.Single(await AutomationApiFixture.ApiBatchesAsync(h)).Status);
    }
}
