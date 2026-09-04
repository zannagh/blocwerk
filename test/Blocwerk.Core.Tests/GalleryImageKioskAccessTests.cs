using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Web.Endpoints;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The gallery byte route's own inner gate, <see cref="WallGalleryImageEndpoint.HasWallAccessAsync"/>.
/// It runs AFTER the <c>WallGalleryImage</c> policy has already admitted the caller, so a registered
/// kiosk tablet reaches it with nobody signed in — and it used to throw
/// <see cref="UnauthorizedAccessException"/> there, which the handler turned into a 401 for exactly
/// the resting-state tablet the panel-photo route serves fine. These pin that the gallery gate now
/// mirrors the panel gate: the kiosk sees its OWN wall, and every other anonymous caller is refused.
/// </summary>
public class GalleryImageKioskAccessTests
{
    [Fact]
    public async Task HasWallAccess_ForAnAnonymousKioskOnItsOwnWall_IsTrue()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        GoAnonymous(h);

        var allowed = await WallGalleryImageEndpoint.HasWallAccessAsync(
            h.WallId,
            token: null,
            h.WallService,
            h.CurrentUser,
            h.DbContextFactory,
            KioskContextFor(isKiosk: true, wallId: h.WallId),
            CancellationToken.None);

        Assert.True(allowed);
    }

    [Fact]
    public async Task HasWallAccess_ForAnAnonymousKioskAimedAtAnotherWall_IsFalse()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        GoAnonymous(h);

        // The device is registered to some other wall, so asking for h.WallId is an ordinary
        // anonymous read again — refused, not widened.
        var allowed = await WallGalleryImageEndpoint.HasWallAccessAsync(
            h.WallId,
            token: null,
            h.WallService,
            h.CurrentUser,
            h.DbContextFactory,
            KioskContextFor(isKiosk: true, wallId: Guid.NewGuid()),
            CancellationToken.None);

        Assert.False(allowed);
    }

    [Fact]
    public async Task HasWallAccess_ForAnOrdinaryAnonymousVisitor_IsFalse()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        GoAnonymous(h);

        // Nobody signed in, no token, not a kiosk: the gate returns false so the endpoint answers a
        // 404, not the spurious 401 the old throwing gate produced.
        var allowed = await WallGalleryImageEndpoint.HasWallAccessAsync(
            h.WallId,
            token: null,
            h.WallService,
            h.CurrentUser,
            h.DbContextFactory,
            KioskContextFor(isKiosk: false, wallId: null),
            CancellationToken.None);

        Assert.False(allowed);
    }

    [Fact]
    public async Task HasWallAccess_ForASignedInMember_IsStillTrue()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        // The owner is the acting user by default; the kiosk fallback must not have narrowed the
        // ordinary membership path.
        var allowed = await WallGalleryImageEndpoint.HasWallAccessAsync(
            h.WallId,
            token: null,
            h.WallService,
            h.CurrentUser,
            h.DbContextFactory,
            KioskContextFor(isKiosk: false, wallId: null),
            CancellationToken.None);

        Assert.True(allowed);
    }

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
}
