using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A boulder flagged <see cref="Boulder.NeedsReview"/> asks its setter to look at it against the new
/// photos. Concluding that nothing needs changing IS a review outcome, so opening Revise and saving
/// without touching anything has to clear the flag — even though that revision is, hold for hold, a
/// no-op and therefore indistinguishable from an offline replay by content alone.
/// </summary>
public class BoulderReviewSignoffTests
{
    [Fact]
    public async Task ReviseBoulder_WithNothingChanged_ClearsNeedsReview_OnLiveBoulder()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 2);

        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "Live", "6A", [new BoulderHoldInput(holds[0].Id, HoldType.Start)]);

        await using (var db = h.CreateContext())
        {
            var b = await db.Boulders.FirstAsync(x => x.Id == boulder.Id);
            b.NeedsReview = true;
            await db.SaveChangesAsync();
        }

        // Identical holds, name, grade and rules: the setter signed it off without editing anything.
        await h.BoulderService.ReviseBoulderAsync(
            boulder.Id, [new BoulderHoldInput(holds[0].Id, HoldType.Start)], name: "Live", grade: "6A");

        await using var check = h.CreateContext();
        var saved = await check.Boulders
            .Include(b => b.BoulderHolds)
            .FirstAsync(b => b.Id == boulder.Id);

        Assert.False(saved.NeedsReview);
        Assert.False(saved.IsHistoric);
        Assert.Equal(holds[0].Id, Assert.Single(saved.BoulderHolds).HoldId);
    }

    [Fact]
    public async Task ReviseBoulder_SignedOffTwice_IsStillANoOp()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 2);

        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "Live", "6A", [new BoulderHoldInput(holds[0].Id, HoldType.Start)]);

        await using (var db = h.CreateContext())
        {
            var b = await db.Boulders.FirstAsync(x => x.Id == boulder.Id);
            b.NeedsReview = true;
            await db.SaveChangesAsync();
        }

        List<BoulderHoldInput> same = [new BoulderHoldInput(holds[0].Id, HoldType.Start)];

        var first = await h.BoulderService.ReviseBoulderAsync(boulder.Id, same, name: "Live", grade: "6A");

        // The replayed call must not throw and must not disturb the boulder: the first pass already
        // left the flag false, so the second writes nothing at all.
        var replay = await h.BoulderService.ReviseBoulderAsync(boulder.Id, same, name: "Live", grade: "6A");

        Assert.Equal(first.Id, replay.Id);
        Assert.False(replay.NeedsReview);

        await using var check = h.CreateContext();
        var saved = await check.Boulders
            .Include(b => b.BoulderHolds)
            .FirstAsync(b => b.Id == boulder.Id);
        Assert.False(saved.NeedsReview);
        Assert.Equal(holds[0].Id, Assert.Single(saved.BoulderHolds).HoldId);
    }

    [Fact]
    public async Task ReviseBoulder_NoOpOnAnUnflaggedBoulder_WritesNothing()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 2);

        // A boulder carries no timestamp that a write would move, so "did this save?" is observed
        // through the same seam production uses to evict caches: the DomainChangeInterceptor only
        // publishes when SaveChanges actually ran.
        var notifier = new RecordingDomainChangeNotifier();
        var factory = new InterceptingDbContextFactory(h.DbContextFactory.ConnectionString, notifier);
        var service = new BoulderService(
            factory, h.CurrentUser, h.ActivityLog, NullLogger<BoulderService>.Instance);

        var boulder = await service.CreateBoulderAsync(
            h.WallId, "Live", "6A", [new BoulderHoldInput(holds[0].Id, HoldType.Start)]);
        Assert.False(boulder.NeedsReview);

        notifier.Changes.Clear();

        await service.ReviseBoulderAsync(
            boulder.Id, [new BoulderHoldInput(holds[0].Id, HoldType.Start)], name: "Live", grade: "6A");

        Assert.Empty(notifier.Changes);
    }

    [Fact]
    public async Task ReviseBoulder_NoOpSignoff_PublishesTheBoulderChange_SoCachesEvict()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 2);

        var notifier = new RecordingDomainChangeNotifier();
        var factory = new InterceptingDbContextFactory(h.DbContextFactory.ConnectionString, notifier);
        var service = new BoulderService(
            factory, h.CurrentUser, h.ActivityLog, NullLogger<BoulderService>.Instance);

        var boulder = await service.CreateBoulderAsync(
            h.WallId, "Live", "6A", [new BoulderHoldInput(holds[0].Id, HoldType.Start)]);

        await using (var db = h.CreateContext())
        {
            var b = await db.Boulders.FirstAsync(x => x.Id == boulder.Id);
            b.NeedsReview = true;
            await db.SaveChangesAsync();
        }

        notifier.Changes.Clear();

        await service.ReviseBoulderAsync(
            boulder.Id, [new BoulderHoldInput(holds[0].Id, HoldType.Start)], name: "Live", grade: "6A");

        // Without a published change the review badge would linger until a manual reload.
        Assert.Contains(
            notifier.Changes,
            c => c.Scope == DomainChangeScope.Boulder && c.BoulderId == boulder.Id);
    }

    [Fact]
    public async Task ReviseBoulder_NoOpSignoff_IsRefusedForAPlainMember()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 2);

        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "Live", "6A", [new BoulderHoldInput(holds[0].Id, HoldType.Start)]);

        await using (var db = h.CreateContext())
        {
            var b = await db.Boulders.FirstAsync(x => x.Id == boulder.Id);
            b.NeedsReview = true;
            await db.SaveChangesAsync();
        }

        // Neither creator, setter nor wall admin: signing a review off is an edit like any other.
        var other = await h.AddMemberAsync("member@test", WallRole.Member);
        h.CurrentUser.GetCurrentUserAsync().Returns(_ => Task.FromResult(other));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.BoulderService.ReviseBoulderAsync(
                boulder.Id, [new BoulderHoldInput(holds[0].Id, HoldType.Start)], name: "Live", grade: "6A"));
        Assert.Equal(BoulderService.CreatorOrAdminRevisionMessage, ex.Message);

        await using var check = h.CreateContext();
        Assert.True((await check.Boulders.FirstAsync(b => b.Id == boulder.Id)).NeedsReview);
    }
}

/// <summary>Collects every change the interceptor publishes, so a test can assert on writes.</summary>
public sealed class RecordingDomainChangeNotifier : IDomainChangeNotifier
{
    public event Action<DomainChange>? Changed;

    public List<DomainChange> Changes { get; } = [];

    public void Publish(DomainChange change)
    {
        Changes.Add(change);
        Changed?.Invoke(change);
    }
}

/// <summary>
/// Like <see cref="TestDbContextFactory"/>, but with the production
/// <see cref="DomainChangeInterceptor"/> wired in so its publishes can be observed.
/// </summary>
public sealed class InterceptingDbContextFactory : IDbContextFactory<BlocwerkDbContext>
{
    private readonly string connectionString;
    private readonly DomainChangeInterceptor interceptor;

    public InterceptingDbContextFactory(string connectionString, IDomainChangeNotifier notifier)
    {
        this.connectionString = connectionString;
        interceptor = new DomainChangeInterceptor(notifier);
    }

    public BlocwerkDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<BlocwerkDbContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(interceptor)
            .Options;

        return new SqliteBlocwerkDbContext(options);
    }
}
