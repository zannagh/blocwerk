using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The central cap that keeps a kiosk session on its own wall: the <see cref="Wall"/> query filter's
/// kiosk gate, and the factory that stamps it on every context so no service has to remember to.
/// </summary>
public class KioskWallScopeTests
{
    [Fact]
    public async Task WallFilter_WithoutKioskWallId_BehavesExactlyAsBefore()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        await SeedSecondWallAsync(h, h.Owner.Id);

        await using var db = h.DbContextFactory.CreateDbContext();

        // Anonymous (Guid.Empty) still disables the membership gate: every wall is visible, which is
        // what the share-token and validation paths rely on.
        db.CurrentUserId = Guid.Empty;
        Assert.Equal(2, await db.Walls.CountAsync());

        // A signed-in member still sees every wall they belong to — both, here.
        db.CurrentUserId = h.Owner.Id;
        Assert.Equal(2, await db.Walls.CountAsync());
    }

    [Fact]
    public async Task WallFilter_WithKioskWallId_ReturnsOnlyThatWall_EvenForAMemberOfBoth()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var otherWallId = await SeedSecondWallAsync(h, h.Owner.Id);

        await using var db = h.DbContextFactory.CreateDbContext();
        db.CurrentUserId = h.Owner.Id;
        db.KioskWallId = h.WallId;

        var visible = await db.Walls.Select(w => w.Id).ToListAsync();
        Assert.Equal([h.WallId], visible);
        Assert.Null(await db.Walls.FirstOrDefaultAsync(w => w.Id == otherWallId));
    }

    [Fact]
    public async Task WallFilter_WithKioskWallId_AlsoNarrowsTheAnonymousFailOpenPath()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        await SeedSecondWallAsync(h, h.Owner.Id);

        await using var db = h.DbContextFactory.CreateDbContext();

        // The membership gate is disabled here — this is the anonymous kiosk browsing case — so the
        // kiosk gate is the ONLY thing standing between the tablet and every other wall.
        db.CurrentUserId = Guid.Empty;
        db.KioskWallId = h.WallId;

        Assert.Equal([h.WallId], await db.Walls.Select(w => w.Id).ToListAsync());
    }

    [Fact]
    public async Task WallFilter_WithAnEmptyKioskWallId_SeesNothing()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        await using var db = h.DbContextFactory.CreateDbContext();
        db.CurrentUserId = h.Owner.Id;

        // Guid.Empty is the fail-CLOSED value the factory uses when a session is known to be a kiosk
        // but its wall could not be determined. It must restrict to nothing, never disable the gate.
        db.KioskWallId = Guid.Empty;

        Assert.Empty(await db.Walls.ToListAsync());
    }

    [Fact]
    public void ScopedFactory_StampsTheKioskWall_OnEveryContextItCreates()
    {
        using var h = new WallTestHarness();
        var wallId = Guid.NewGuid();
        var factory = new KioskScopedDbContextFactory(
            new StubRootFactory(h.DbContextFactory),
            StubKiosk(isKiosk: true, wallId));

        using var first = factory.CreateDbContext();
        using var second = factory.CreateDbContext();

        Assert.Equal(wallId, first.KioskWallId);
        Assert.Equal(wallId, second.KioskWallId);
    }

    [Fact]
    public void ScopedFactory_LeavesOrdinarySessionsUnstamped()
    {
        using var h = new WallTestHarness();
        var factory = new KioskScopedDbContextFactory(
            new StubRootFactory(h.DbContextFactory),
            StubKiosk(isKiosk: false, wallId: null));

        using var db = factory.CreateDbContext();

        Assert.Null(db.KioskWallId);
    }

    [Fact]
    public void ScopedFactory_StampsFailClosed_WhenAKioskHasNoWall()
    {
        using var h = new WallTestHarness();
        var factory = new KioskScopedDbContextFactory(
            new StubRootFactory(h.DbContextFactory),
            StubKiosk(isKiosk: true, wallId: null));

        using var db = factory.CreateDbContext();

        // Not null (which would mean "no kiosk, see everything") but empty, which matches no wall.
        Assert.Equal(Guid.Empty, db.KioskWallId);
    }

    [Fact]
    public async Task ApiKeyMinting_IsRefusedForAKioskSession_ButValidationStillWorks()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        var (_, token) = await h.ApiKeyService.CreateKioskKeyAsync(h.WallId, h.Owner.Id, "Tablet", null);
        var guarded = new KioskGuardedApiKeyService(h.ApiKeyService, StubKiosk(isKiosk: true, h.WallId));

        await Assert.ThrowsAsync<KioskRestrictedException>(
            () => guarded.CreateWallKeyAsync(h.WallId, h.Owner.Id, "Sensor", null));
        await Assert.ThrowsAsync<KioskRestrictedException>(
            () => guarded.CreateKioskKeyAsync(h.WallId, h.Owner.Id, "Another tablet", null));
        await Assert.ThrowsAsync<KioskRestrictedException>(
            () => guarded.CreateUserKeyAsync(h.Owner.Id, h.Owner.Id, "Personal", null));

        // Registration must keep working from a tablet that is already registered, and reads and
        // revocations are deliberately left alone.
        Assert.Equal(h.WallId, await guarded.ValidateKioskAsync(token));
        Assert.NotEmpty(await guarded.GetWallKeysAsync(h.WallId, h.Owner.Id));
    }

    [Fact]
    public async Task ApiKeyMinting_IsUnaffectedForAnOrdinarySession()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        var guarded = new KioskGuardedApiKeyService(h.ApiKeyService, StubKiosk(isKiosk: false, wallId: null));

        var (key, _) = await guarded.CreateWallKeyAsync(h.WallId, h.Owner.Id, "Sensor", null);
        Assert.Equal(ApiKeyScope.Wall, key.Scope);
    }

    /// <summary>
    /// An activity list is the user's OWN record and is never filtered by wall, but the wall behind a
    /// row can be invisible here — that is exactly what the kiosk gate on the Wall filter does. The
    /// row therefore survives with a null <c>Wall</c> navigation, and rendering that as nothing made
    /// a session at another gym read as a training-only one. It is labelled instead, without naming
    /// the wall the tablet may not see.
    /// </summary>
    [Fact]
    public async Task ActivityList_LabelsAWallThisSessionCannotSee_WithoutNamingIt()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var otherWallId = await SeedSecondWallAsync(h, h.Owner.Id);

        await SeedActivityAsync(h, h.WallId);
        await SeedActivityAsync(h, otherWallId);
        await SeedActivityAsync(h, wallId: null);

        var kiosk = new ProgressionService(
            new KioskScopedDbContextFactory(new StubRootFactory(h.DbContextFactory), StubKiosk(isKiosk: true, h.WallId)),
            h.CurrentUser,
            h.WallService,
            NullLogger<ProgressionService>.Instance);

        var labels = (await kiosk.GetActivitiesAsync()).Select(a => a.WallName).ToList();

        // Every row is still there, and none of them is blank-but-not-null.
        Assert.Equal(3, labels.Count);
        Assert.Contains("Test Wall", labels);
        Assert.Contains("another wall", labels);
        Assert.Contains(null, labels);

        // The other wall's name is never handed to the tablet.
        Assert.DoesNotContain("Second Wall", labels);

        // An ordinary session is untouched: it sees both walls, so both are named.
        var ordinary = new ProgressionService(
            h.DbContextFactory, h.CurrentUser, h.WallService, NullLogger<ProgressionService>.Instance);

        var ordinaryLabels = (await ordinary.GetActivitiesAsync()).Select(a => a.WallName).ToList();
        Assert.Contains("Test Wall", ordinaryLabels);
        Assert.Contains("Second Wall", ordinaryLabels);
        Assert.Contains(null, ordinaryLabels);
        Assert.DoesNotContain("another wall", ordinaryLabels);
    }

    private static async Task SeedActivityAsync(WallTestHarness h, Guid? wallId)
    {
        await using var db = h.CreateContext();
        var started = DateTimeOffset.UtcNow.AddDays(-1);
        db.Activities.Add(new Activity
        {
            UserId = h.Owner.Id,
            WallId = wallId,
            StartedAt = started,
            LastEventAt = started.AddHours(1),
        });
        await db.SaveChangesAsync();
    }

    private static IKioskContext StubKiosk(bool isKiosk, Guid? wallId)
    {
        var context = Substitute.For<IKioskContext>();
        context.IsKiosk.Returns(isKiosk);
        context.KioskWallId.Returns(wallId);
        return context;
    }

    private static async Task<Guid> SeedSecondWallAsync(WallTestHarness h, Guid ownerId)
    {
        await using var db = h.CreateContext();
        var wall = new Wall
        {
            Name = "Second Wall",
            OwnerId = ownerId,
            Photo = [1],
            PhotoContentType = "image/jpeg",
        };
        db.Walls.Add(wall);
        db.WallMembers.Add(new WallMember { WallId = wall.Id, UserId = ownerId, Role = WallRole.Admin });
        await db.SaveChangesAsync();
        return wall.Id;
    }

    /// <summary>
    /// Stands in for <see cref="RootDbContextFactory"/>, which can only build a Postgres context from
    /// the app's own options. The decorator under test only ever calls <c>CreateDbContext</c>.
    /// </summary>
    private sealed class StubRootFactory : RootDbContextFactory
    {
        private readonly IDbContextFactory<BlocwerkDbContext> inner;

        public StubRootFactory(IDbContextFactory<BlocwerkDbContext> inner)
            : base(new DbContextOptionsBuilder<BlocwerkDbContext>().Options)
        {
            this.inner = inner;
        }

        public override BlocwerkDbContext CreateDbContext() => inner.CreateDbContext();
    }
}
