using Blocwerk.Authentication.Kiosk;
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
/// The tablet's RESTING state: a registered kiosk, on the wall it is bolted to, with nobody picked.
/// </summary>
/// <remarks>
/// This was a hole between two layers. The DATA layer had always allowed it — the wall query filter
/// fails open at <see cref="Guid.Empty"/> and the kiosk stamp pins the read to the one wall — but the
/// READ SERVICES demanded a signed-in user, so every anonymous call threw and the pages above turned
/// that into a redirect to <c>/account/login</c>. On a paired tablet that produced the reported loop:
/// the login page sent it to <c>/oauth-select</c>, and every route back to the wall threw again.
/// <para>
/// The tests below pin BOTH directions, because widening this is only safe if the widening is exact:
/// the anonymous kiosk can read its OWN wall, and every other anonymous caller — including a kiosk
/// pointed at somebody else's wall — is refused exactly as before.
/// </para>
/// </remarks>
public class KioskAnonymousViewingTests
{
    [Fact]
    public void ViewableWallId_ForAnOrdinaryAnonymousVisitor_IsNull()
    {
        Assert.Null(KioskViewing.ViewableWallId(null));
        Assert.Null(KioskViewing.ViewableWallId(KioskContextFor(isKiosk: false, wallId: Guid.NewGuid())));
    }

    [Fact]
    public void ViewableWallId_ForAKioskWhoseWallIsUnknown_IsNull()
    {
        // Guid.Empty is the fail-CLOSED value a device re-registered mid-session resolves to. It must
        // never widen into "any wall" here, which is precisely what an unguarded Guid.Empty would do
        // once it reached the query filter's membership gate.
        Assert.Null(KioskViewing.ViewableWallId(KioskContextFor(isKiosk: true, wallId: Guid.Empty)));
        Assert.Null(KioskViewing.ViewableWallId(KioskContextFor(isKiosk: true, wallId: null)));
    }

    [Fact]
    public void AllowsAnonymousViewOf_IsTrueForItsOwnWallAndFalseForEveryOther()
    {
        var wallId = Guid.NewGuid();
        var kiosk = KioskContextFor(isKiosk: true, wallId: wallId);

        Assert.True(KioskViewing.AllowsAnonymousViewOf(kiosk, wallId));
        Assert.False(KioskViewing.AllowsAnonymousViewOf(kiosk, Guid.NewGuid()));
        Assert.False(KioskViewing.AllowsAnonymousViewOf(kiosk, Guid.Empty));

        // The ordinary anonymous visitor, who must still be sent to sign in.
        Assert.False(KioskViewing.AllowsAnonymousViewOf(null, wallId));
    }

    [Fact]
    public async Task GetWallAsync_ForAnAnonymousKioskOnItsOwnWall_ReturnsTheWall()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var walls = AnonymousKioskWallService(h, h.WallId);

        // The load that used to throw, which is the whole redirect loop in one call.
        var wall = await walls.GetWallAsync(h.WallId);

        Assert.NotNull(wall);
        Assert.Equal(h.WallId, wall!.Id);
    }

    [Fact]
    public async Task GetWallAsync_ForAnAnonymousVisitorWhoIsNotAKiosk_StillThrows()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        GoAnonymous(h);
        var walls = new WallService(
            h.DbContextFactory,
            h.CurrentUser,
            h.HoldDetection,
            h.ImageAlignment,
            h.ActivityLog,
            NullLogger<WallService>.Instance);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => walls.GetWallAsync(h.WallId));
    }

    [Fact]
    public async Task GetWallAsync_ForAnAnonymousKioskAimedAtAnotherWall_IsRefused()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var otherWallId = await SeedSecondWallAsync(h);

        // The device is registered to h.WallId and asks for the other one. The allowance is keyed on
        // the wall, so this is an ordinary anonymous read again — refused, not widened.
        var walls = AnonymousKioskWallService(h, h.WallId);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => walls.GetWallAsync(otherWallId));
    }

    [Fact]
    public async Task GetPhotoAsync_ForAnAnonymousKioskOnItsOwnWall_ReturnsTheBytes()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var walls = AnonymousKioskWallService(h, h.WallId);

        // The photo IS the wall page: without this the page renders and every image 500s, which on a
        // tablet is indistinguishable from the wall being broken.
        Assert.NotNull(await walls.GetPhotoAsync(h.WallId));
    }

    [Fact]
    public async Task GetBoulderAsync_ForAnAnonymousKioskOnItsOwnWall_ReturnsTheBoulder()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var boulderId = await SeedBoulderAsync(h, h.WallId);
        var boulders = AnonymousKioskBoulderService(h, h.WallId);

        // Opening a boulder is the tablet's whole job, so this must work with nobody picked.
        var boulder = await boulders.GetBoulderAsync(boulderId);

        Assert.NotNull(boulder);
        Assert.Equal(boulderId, boulder!.Id);
    }

    [Fact]
    public async Task GetBoulderAsync_ForAnAnonymousKiosk_CannotReachABoulderOnAnotherWall()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var otherWallId = await SeedSecondWallAsync(h);
        var strangerBoulderId = await SeedBoulderAsync(h, otherWallId);

        // Boulder carries no query filter of its own — the read reaches through to db.Walls, and the
        // stamped kiosk wall is what keeps the anonymous fail-open from becoming "every boulder in
        // the installation".
        var boulders = AnonymousKioskBoulderService(h, h.WallId);

        Assert.Null(await boulders.GetBoulderAsync(strangerBoulderId));
    }

    [Fact]
    public async Task GetBoulderAsync_ForAnAnonymousVisitorWhoIsNotAKiosk_StillThrows()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var boulderId = await SeedBoulderAsync(h, h.WallId);
        GoAnonymous(h);
        var boulders = new BoulderService(
            h.DbContextFactory,
            h.CurrentUser,
            h.ActivityLog,
            NullLogger<BoulderService>.Instance);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => boulders.GetBoulderAsync(boulderId));
    }

    [Fact]
    public void TheWallPage_TheKioskIsBouncedBackTo_IsItselfReachable()
    {
        // The loop had an outer edge as well as an inner one: the middleware answers a blocked GET
        // with a redirect to /walls/{id}?kiosk_blocked=1, so that path must be allowed and its page
        // routable — otherwise the bounce lands on another bounce. Pinned here so neither list can
        // be edited into a ping-pong again.
        Assert.False(KioskRestrictions.IsBlockedPath($"/walls/{Guid.NewGuid()}"));
        Assert.Contains("Blocwerk.Web.Components.Pages.Walls.WallDetail", KioskRestrictions.AllowedPageTypes);
        Assert.Contains("Blocwerk.Web.Components.Pages.Walls.BoulderDetail", KioskRestrictions.AllowedPageTypes);

        // And the picker, which is how anybody becomes the acting user in the first place.
        Assert.False(KioskRestrictions.IsBlockedPath("/kiosk/users"));
        Assert.Contains("Blocwerk.Web.Components.Pages.Kiosk.KioskUsers", KioskRestrictions.AllowedPageTypes);
    }

    /// <summary>Makes the harness's current-user service answer as an anonymous caller.</summary>
    private static void GoAnonymous(WallTestHarness h)
    {
        h.CurrentUser.GetCurrentUserAsync()
            .Returns(_ => Task.FromException<User>(new UnauthorizedAccessException()));
    }

    private static IKioskContext KioskContextFor(bool isKiosk, Guid? wallId)
    {
        var kiosk = Substitute.For<IKioskContext>();
        kiosk.IsKiosk.Returns(isKiosk);
        kiosk.KioskWallId.Returns(wallId);
        return kiosk;
    }

    private static WallService AnonymousKioskWallService(WallTestHarness h, Guid kioskWallId)
    {
        GoAnonymous(h);
        var kiosk = KioskContextFor(isKiosk: true, wallId: kioskWallId);
        return new WallService(
            new KioskStampingFactory(h.DbContextFactory, kiosk),
            h.CurrentUser,
            h.HoldDetection,
            h.ImageAlignment,
            h.ActivityLog,
            NullLogger<WallService>.Instance,
            kiosk);
    }

    private static BoulderService AnonymousKioskBoulderService(WallTestHarness h, Guid kioskWallId)
    {
        GoAnonymous(h);
        var kiosk = KioskContextFor(isKiosk: true, wallId: kioskWallId);
        return new BoulderService(
            new KioskStampingFactory(h.DbContextFactory, kiosk),
            h.CurrentUser,
            h.ActivityLog,
            NullLogger<BoulderService>.Instance,
            kiosk);
    }

    private static async Task<Guid> SeedSecondWallAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var wall = new Wall
        {
            Name = "Second Wall",
            OwnerId = h.Owner.Id,
            Photo = [1],
            PhotoContentType = "image/jpeg",
        };
        db.Walls.Add(wall);
        db.WallMembers.Add(new WallMember { WallId = wall.Id, UserId = h.Owner.Id, Role = WallRole.Admin });
        await db.SaveChangesAsync();
        return wall.Id;
    }

    private static async Task<Guid> SeedBoulderAsync(WallTestHarness h, Guid wallId)
    {
        await using var db = h.CreateContext();
        var boulder = new Boulder
        {
            WallId = wallId,
            Name = "Test Boulder",
            CreatedByUserId = h.Owner.Id,
        };
        db.Boulders.Add(boulder);
        await db.SaveChangesAsync();
        return boulder.Id;
    }

    /// <summary>
    /// The test stand-in for <c>KioskScopedDbContextFactory</c>: stamps every context it hands out
    /// with the tablet's wall, exactly as production does. Without it these tests would exercise the
    /// anonymous fail-open with no kiosk gate behind it — which is the one thing that must never
    /// happen in production and so must not be simulated here either.
    /// </summary>
    private sealed class KioskStampingFactory : IDbContextFactory<BlocwerkDbContext>
    {
        private readonly IDbContextFactory<BlocwerkDbContext> inner;
        private readonly IKioskContext kioskContext;

        public KioskStampingFactory(IDbContextFactory<BlocwerkDbContext> inner, IKioskContext kioskContext)
        {
            this.inner = inner;
            this.kioskContext = kioskContext;
        }

        public BlocwerkDbContext CreateDbContext()
        {
            var db = inner.CreateDbContext();
            if (kioskContext.IsKiosk)
            {
                db.KioskWallId = kioskContext.KioskWallId ?? Guid.Empty;
            }

            return db;
        }
    }
}
