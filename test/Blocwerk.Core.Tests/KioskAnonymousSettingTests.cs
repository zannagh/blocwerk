using Blocwerk.Authentication.Kiosk;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The app's ONE unauthenticated write: a boulder set at a wall-mounted tablet with nobody signed in.
/// </summary>
/// <remarks>
/// Every test here asserts BEHAVIOUR, not the absence of an exception: what got written, who it was
/// credited to, and — for the refusals — that nothing was written at all. The four conditions in
/// <see cref="KioskAnonymousSetting"/> are pinned one at a time, because a grant that holds only
/// because two checks happen to overlap is a grant that breaks the moment one of them moves.
/// </remarks>
public class KioskAnonymousSettingTests
{
    [Fact]
    public async Task GhostRow_IsSeededWithTheModel()
    {
        using var h = new WallTestHarness();
        await using var db = h.CreateContext();

        // Seeded through HasData, so it arrives with EnsureCreated here and with the migration in
        // production. Boulder.CreatedByUserId is a required, restrict-deleted FK: if this row can
        // ever be missing, an anonymous create has nothing to point at.
        var ghost = await db.Users.FirstOrDefaultAsync(u => u.Id == GhostUser.Id);

        Assert.NotNull(ghost);
        Assert.Equal(GhostUser.Identifier, ghost!.Identifier);
        Assert.Equal(PlaceholderIdentity.DisplayName, ghost.Name);
        Assert.Equal(IdentityRole.User, ghost.Role);
    }

    [Fact]
    public async Task WithNoKioskRegistration_TheCreateIsRefusedAndNothingIsWritten()
    {
        using var fixture = await AnonymousSettingFixture.CreateAsync();
        await fixture.AllowAnonymousSettingAsync();

        // An ordinary signed-out visitor who simply navigated to the create URL.
        var boulders = fixture.BoulderServiceFor(AnonymousSettingFixture.KioskContextFor(isKiosk: false, wallId: null, keyId: null));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.CreateAsync(boulders));
        Assert.Empty(await fixture.StoredBouldersAsync());
    }

    [Fact]
    public async Task ForAWallOtherThanTheOneInTheCookie_TheCreateIsRefused()
    {
        using var fixture = await AnonymousSettingFixture.CreateAsync();
        await fixture.AllowAnonymousSettingAsync();
        var otherWallId = await fixture.SeedSecondWallAsync(allowAnonymousSetting: true);

        // The tablet is registered to the seeded wall and asks for the neighbouring one — which has
        // the opt-in ON, so only the wall comparison can be refusing this.
        var boulders = fixture.BoulderServiceFor(fixture.LiveKioskContext());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => fixture.CreateAsync(boulders, wallId: otherWallId));
        Assert.Empty(await fixture.StoredBouldersAsync(otherWallId));
    }

    [Fact]
    public async Task WhenTheKioskKeyHasBeenRevoked_TheCreateIsRefused()
    {
        using var fixture = await AnonymousSettingFixture.CreateAsync();
        await fixture.AllowAnonymousSettingAsync();

        // It works right up until the admin revokes, and stops afterwards: the device cookie is
        // unchanged throughout, so only the database re-check can be the difference.
        await fixture.CreateAsync(
            fixture.BoulderServiceFor(fixture.LiveKioskContext()), name: "Before Revocation");
        Assert.Single(await fixture.StoredBouldersAsync());

        await fixture.Harness.ApiKeyService.RevokeAsync(fixture.KioskKey.Id, fixture.Harness.Owner.Id);

        // A FRESH service, i.e. the next request or circuit — which is the scope KioskKeyValidator
        // is registered at, and therefore the scope its 15-second positive cache lives in. A tablet
        // that already validated this second keeps working for at most that long; the next one does
        // not, and that window is the documented trade in KioskKeyValidator.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => fixture.CreateAsync(
                fixture.BoulderServiceFor(fixture.LiveKioskContext()), name: "After Revocation"));
        Assert.Single(await fixture.StoredBouldersAsync());
    }

    [Fact]
    public async Task WhenTheWallHasNotOptedIn_TheCreateIsRefused()
    {
        using var fixture = await AnonymousSettingFixture.CreateAsync();
        var boulders = fixture.BoulderServiceFor(fixture.LiveKioskContext());

        // The default. A paired tablet on a wall nobody switched this on for is a read-only tablet,
        // which is the whole point of making it opt-in.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.CreateAsync(boulders));
        Assert.Empty(await fixture.StoredBouldersAsync());

        // And it starts working the moment an admin does switch it on, so the refusal above is the
        // toggle and not something incidental.
        await fixture.AllowAnonymousSettingAsync();
        await fixture.CreateAsync(boulders);
        Assert.Single(await fixture.StoredBouldersAsync());
    }

    [Fact]
    public async Task WithNoKeyValidator_TheCreateFailsClosed()
    {
        using var fixture = await AnonymousSettingFixture.CreateAsync();
        await fixture.AllowAnonymousSettingAsync();

        // A host with no auth stack cannot re-check the key against the database, so it must not be
        // able to grant on the cookie's word alone.
        var boulders = fixture.BoulderServiceFor(fixture.LiveKioskContext(), withKeyValidator: false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.CreateAsync(boulders));
        Assert.Empty(await fixture.StoredBouldersAsync());
    }

    [Fact]
    public async Task WithNobodyPicked_TheBoulderIsStoredAndReadsAsGhost()
    {
        using var fixture = await AnonymousSettingFixture.CreateAsync();
        await fixture.AllowAnonymousSettingAsync();
        var boulders = fixture.BoulderServiceFor(fixture.LiveKioskContext());

        var created = await fixture.CreateAsync(boulders, name: "Unclaimed Line");

        var stored = Assert.Single(await fixture.StoredBouldersAsync());
        Assert.Equal(created.Id, stored.Id);
        Assert.Equal("Unclaimed Line", stored.Name);
        Assert.False(stored.IsDraft);

        // Credited to the Ghost system row — nobody's user id, so every "is this mine?" check in the
        // app (archive, grade proposals, the mine-only filter) still answers false for everyone.
        Assert.Equal(GhostUser.Id, stored.CreatedByUserId);
        Assert.Empty(stored.Setters);

        // And it RENDERS as Ghost, through the one shared formatter both the wall list and the
        // boulder detail page use — never the system row's raw name by some other route.
        await using var db = fixture.Harness.CreateContext();
        var creator = await db.Users.FirstAsync(u => u.Id == stored.CreatedByUserId);
        Assert.Equal(PlaceholderIdentity.DisplayName, BoulderSetterNames.Describe(null, creator));
    }

    [Fact]
    public async Task WithAConsentingSetterPicked_TheBoulderIsCreditedToThem()
    {
        using var fixture = await AnonymousSettingFixture.CreateAsync();
        await fixture.AllowAnonymousSettingAsync();
        var setter = await fixture.AddConsentingMemberAsync("setter@test");
        var boulders = fixture.BoulderServiceFor(fixture.LiveKioskContext());

        await fixture.CreateAsync(boulders, setterUserIds: [setter.Id]);

        var stored = Assert.Single(await fixture.StoredBouldersAsync());

        // The creator stays Ghost — nobody signed in — but the CREDIT goes to the person who was
        // ticked, which is what the wall shows.
        Assert.Equal(GhostUser.Id, stored.CreatedByUserId);
        var recorded = Assert.Single(stored.Setters);
        Assert.Equal(setter.Id, recorded.UserId);
        Assert.Equal(setter.Name, BoulderSetterNames.Describe([setter.Name], null));
    }

    [Fact]
    public async Task AMemberWhoNeverConsented_CannotBeCredited()
    {
        using var fixture = await AnonymousSettingFixture.CreateAsync();
        await fixture.AllowAnonymousSettingAsync();

        // A member of this very wall, so the ordinary create path's member check would have let this
        // through. Consent is the extra gate an anonymous caller has to clear.
        var silent = await fixture.Harness.AddMemberAsync("silent@test", WallRole.Member);
        var boulders = fixture.BoulderServiceFor(fixture.LiveKioskContext());

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => fixture.CreateAsync(boulders, setterUserIds: [silent.Id]));

        Assert.Equal(KioskAnonymousSetting.UnconsentingSetterMessage, error.Message);

        // Refused outright, not silently dropped: no boulder at all rather than one credited to
        // nobody the setter meant.
        Assert.Empty(await fixture.StoredBouldersAsync());
    }

    [Fact]
    public async Task AConsentingUserOfAnotherWall_CannotBeCredited()
    {
        using var fixture = await AnonymousSettingFixture.CreateAsync();
        await fixture.AllowAnonymousSettingAsync();
        var otherWallId = await fixture.SeedSecondWallAsync(allowAnonymousSetting: false);
        var stranger = await fixture.AddConsentingMemberAsync("stranger@test", otherWallId);
        var boulders = fixture.BoulderServiceFor(fixture.LiveKioskContext());

        // Consenting — but at a different wall's kiosk. Consent is per membership row, so naming the
        // user is not enough.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => fixture.CreateAsync(boulders, setterUserIds: [stranger.Id]));
        Assert.Empty(await fixture.StoredBouldersAsync());
    }

    [Fact]
    public async Task OneGoodSetterDoesNotSmuggleInABadOne()
    {
        using var fixture = await AnonymousSettingFixture.CreateAsync();
        await fixture.AllowAnonymousSettingAsync();
        var good = await fixture.AddConsentingMemberAsync("good@test");
        var boulders = fixture.BoulderServiceFor(fixture.LiveKioskContext());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => fixture.CreateAsync(boulders, setterUserIds: [good.Id, Guid.NewGuid()]));

        Assert.Empty(await fixture.StoredBouldersAsync());
    }

    [Fact]
    public async Task TheTabletIsCappedOnVolume()
    {
        using var fixture = await AnonymousSettingFixture.CreateAsync();
        await fixture.AllowAnonymousSettingAsync();
        var boulders = fixture.BoulderServiceFor(fixture.LiveKioskContext());

        for (var i = 0; i < KioskAnonymousSettingThrottle.MaxPerKey; i++)
        {
            await fixture.CreateAsync(boulders, name: $"Problem {i}");
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => fixture.CreateAsync(boulders, name: "One Too Many"));

        Assert.Equal(
            KioskAnonymousSettingThrottle.MaxPerKey,
            (await fixture.StoredBouldersAsync()).Count);
    }

    [Fact]
    public void TheThrottleFreesUpOnceTheWindowHasPassed()
    {
        var throttle = new KioskAnonymousSettingThrottle();
        var key = Guid.NewGuid();
        var wall = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow;

        for (var i = 0; i < KioskAnonymousSettingThrottle.MaxPerKey; i++)
        {
            Assert.True(throttle.TryRecord(key, wall, start));
        }

        Assert.False(throttle.TryRecord(key, wall, start));

        // A cap, not a ban: a setting crew that comes back an hour later can keep working.
        Assert.True(throttle.TryRecord(key, wall, start.Add(KioskAnonymousSettingThrottle.Window).AddSeconds(1)));
    }

    [Fact]
    public void TheThrottleIsPerKey()
    {
        var throttle = new KioskAnonymousSettingThrottle();
        var wall = Guid.NewGuid();
        var busy = Guid.NewGuid();
        var quiet = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < KioskAnonymousSettingThrottle.MaxPerKey; i++)
        {
            throttle.TryRecord(busy, wall, now);
        }

        Assert.False(throttle.TryRecord(busy, wall, now));

        // A second tablet in the same gym is unaffected — one spammed tablet must not stop the crew
        // working on the other one.
        Assert.True(throttle.TryRecord(quiet, wall, now));
    }

    [Fact]
    public async Task TheSignedInPathIsUnchanged()
    {
        using var fixture = await AnonymousSettingFixture.CreateAsync();

        // No opt-in, no kiosk anything: the ordinary create still works and is still credited to the
        // person who made it. The anonymous branch must not have taken anything away.
        var boulder = await fixture.CreateAsync(fixture.Harness.BoulderService, name: "Ordinary");

        var stored = Assert.Single(await fixture.StoredBouldersAsync());
        Assert.Equal(boulder.Id, stored.Id);
        Assert.Equal(fixture.Harness.Owner.Id, stored.CreatedByUserId);
        Assert.NotEqual(GhostUser.Id, stored.CreatedByUserId);
    }

    [Fact]
    public void TheBylineFormatterPrefersSettersThenCreatorThenGhost()
    {
        var creator = new User { Identifier = "real@test", DisplayName = "Ana" };

        Assert.Equal("Bo & Cy", BoulderSetterNames.Describe(["Bo", "Cy"], creator));
        Assert.Equal("Ana", BoulderSetterNames.Describe(null, creator));
        Assert.Equal(PlaceholderIdentity.DisplayName, BoulderSetterNames.Describe(null, null));
        Assert.Equal(PlaceholderIdentity.DisplayName, BoulderSetterNames.Describe([], GhostUser.Create()));

        // A creator whose name has been scrubbed (a deleted account's tombstone) reads as the
        // placeholder rather than as an empty byline.
        var scrubbed = new User { Identifier = "gone", DisplayName = string.Empty };
        Assert.Equal(PlaceholderIdentity.DisplayName, BoulderSetterNames.Describe(null, scrubbed));
    }

    /// <summary>
    /// Gate condition 1 on its own: the session is not a kiosk at all. Every OTHER condition is made
    /// to pass — the wall has opted in, the api key id is real and the validator says yes — so
    /// nothing but <c>KioskViewing.AllowsAnonymousViewOf</c> can be producing the refusal. Deleting
    /// that check breaks this test and nothing else.
    /// </summary>
    [Fact]
    public async Task WithoutTheKioskRegistration_TheGrantIsRefusedOnItsOwn()
    {
        using var fixture = await AnonymousSettingFixture.CreateAsync();
        await fixture.AllowAnonymousSettingAsync();

        var alwaysValid = Substitute.For<IKioskKeyValidator>();
        alwaysValid.IsKeyValidAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        await using var db = fixture.Harness.CreateContext();

        // Same key, same wall, same opt-in — only IsKiosk differs.
        Assert.True(await KioskAnonymousSetting.IsAllowedAsync(
            db, AnonymousSettingFixture.KioskContextFor(isKiosk: true, fixture.Harness.WallId, fixture.KioskKey.Id),
            alwaysValid, fixture.Harness.WallId));

        Assert.False(await KioskAnonymousSetting.IsAllowedAsync(
            db, AnonymousSettingFixture.KioskContextFor(isKiosk: false, fixture.Harness.WallId, fixture.KioskKey.Id),
            alwaysValid, fixture.Harness.WallId));
    }

    /// <summary>
    /// Gate condition 2 on its own: a kiosk registration that names NO api key. The validator here
    /// says yes to everything, so the key re-validation cannot be what refuses this — only the
    /// non-empty check can.
    /// </summary>
    [Fact]
    public async Task WithNoApiKeyOnTheRegistration_TheGrantIsRefusedOnItsOwn()
    {
        using var fixture = await AnonymousSettingFixture.CreateAsync();
        await fixture.AllowAnonymousSettingAsync();

        var alwaysValid = Substitute.For<IKioskKeyValidator>();
        alwaysValid.IsKeyValidAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        await using var db = fixture.Harness.CreateContext();

        Assert.False(await KioskAnonymousSetting.IsAllowedAsync(
            db, AnonymousSettingFixture.KioskContextFor(isKiosk: true, fixture.Harness.WallId, Guid.Empty),
            alwaysValid, fixture.Harness.WallId));

        Assert.False(await KioskAnonymousSetting.IsAllowedAsync(
            db, AnonymousSettingFixture.KioskContextFor(isKiosk: true, fixture.Harness.WallId, keyId: null),
            alwaysValid, fixture.Harness.WallId));
    }

    /// <summary>
    /// Gate condition 4 on its own, at the same isolated level as the two above: with the key
    /// re-validation forced to pass, the wall's opt-in is the only thing left that can refuse.
    /// </summary>
    [Fact]
    public async Task WithoutTheWallOptIn_TheGrantIsRefusedOnItsOwn()
    {
        using var fixture = await AnonymousSettingFixture.CreateAsync();

        var alwaysValid = Substitute.For<IKioskKeyValidator>();
        alwaysValid.IsKeyValidAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var kiosk = fixture.LiveKioskContext();

        await using (var db = fixture.Harness.CreateContext())
        {
            Assert.False(await KioskAnonymousSetting.IsAllowedAsync(
                db, kiosk, alwaysValid, fixture.Harness.WallId));
        }

        await fixture.AllowAnonymousSettingAsync();

        await using (var db = fixture.Harness.CreateContext())
        {
            Assert.True(await KioskAnonymousSetting.IsAllowedAsync(
                db, kiosk, alwaysValid, fixture.Harness.WallId));
        }
    }

    /// <summary>
    /// The missing-throttle branch, which the key-validator test does not reach: a host wired with a
    /// real validator but NO volume cap must refuse. An unattended write surface with no cap is not
    /// something to default into.
    /// </summary>
    [Fact]
    public async Task WithNoThrottle_TheCreateFailsClosed()
    {
        using var fixture = await AnonymousSettingFixture.CreateAsync();
        await fixture.AllowAnonymousSettingAsync();

        var boulders = fixture.BoulderServiceFor(fixture.LiveKioskContext(), withThrottle: false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.CreateAsync(boulders));
        Assert.Empty(await fixture.StoredBouldersAsync());
    }

    /// <summary>
    /// A refused create must not cost budget. The setter allow-list refuses this one AFTER the gate
    /// has run, and the tablet's whole allowance is still there afterwards.
    /// </summary>
    [Fact]
    public async Task ARefusedCreateDoesNotSpendTheBudget()
    {
        using var fixture = await AnonymousSettingFixture.CreateAsync();
        await fixture.AllowAnonymousSettingAsync();

        var throttle = new KioskAnonymousSettingThrottle();
        var boulders = fixture.BoulderServiceFor(fixture.LiveKioskContext(), throttle: throttle);

        for (var i = 0; i < 5; i++)
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => fixture.CreateAsync(boulders, name: $"Refused {i}", setterUserIds: [Guid.NewGuid()]));
        }

        Assert.Empty(await fixture.StoredBouldersAsync());

        // The full allowance, not five short of it.
        for (var i = 0; i < KioskAnonymousSettingThrottle.MaxPerKey; i++)
        {
            await fixture.CreateAsync(boulders, name: $"Problem {i}");
        }

        Assert.Equal(
            KioskAnonymousSettingThrottle.MaxPerKey,
            (await fixture.StoredBouldersAsync()).Count);
    }

    /// <summary>
    /// The cross-tenant denial cliff. The old shape shared ONE 200/hour counter across the whole
    /// installation, so seven saturated tablets in one gym switched anonymous setting off for every
    /// other gym. Saturating wall A must leave wall B untouched.
    /// </summary>
    [Fact]
    public void SaturatingOneWallDoesNotRefuseAnother()
    {
        var throttle = new KioskAnonymousSettingThrottle();
        var quietWall = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // THREE busy gyms, each spent to its own ceiling across as many tablets as that takes. Three
        // walls' worth is far past the 200 the old single shared counter allowed the entire
        // installation, so a run that got this far under the old shape had already switched the
        // feature off for everybody else.
        for (var wall = 0; wall < 3; wall++)
        {
            var busyWall = Guid.NewGuid();
            var recorded = 0;
            while (recorded < KioskAnonymousSettingThrottle.MaxPerWall)
            {
                var key = Guid.NewGuid();
                for (var i = 0; i < KioskAnonymousSettingThrottle.MaxPerKey
                                && recorded < KioskAnonymousSettingThrottle.MaxPerWall; i++)
                {
                    Assert.True(throttle.TryRecord(key, busyWall, now));
                    recorded++;
                }
            }

            // Saturated: even a brand-new tablet on that wall is now refused, by the WALL cap.
            Assert.Equal(
                KioskAnonymousSettingBudget.WallCapReached,
                throttle.Check(Guid.NewGuid(), busyWall, now));
        }

        // The gym next door is untouched — which is the whole point.
        Assert.Equal(KioskAnonymousSettingBudget.Allowed, throttle.Check(Guid.NewGuid(), quietWall, now));
        Assert.True(throttle.TryRecord(Guid.NewGuid(), quietWall, now));
    }

    /// <summary>
    /// The installation-wide backstop still exists and still bites — it is just set far above any
    /// plausible real load, and it reports itself distinctly so it can be logged as the incident it
    /// is rather than as ordinary throttling.
    /// </summary>
    [Fact]
    public void TheInstallationBackstopStillTrips()
    {
        var throttle = new KioskAnonymousSettingThrottle();
        var now = DateTimeOffset.UtcNow;

        // A fresh wall and key every time, so neither of the tenant caps can be what refuses.
        for (var i = 0; i < KioskAnonymousSettingThrottle.MaxGlobal; i++)
        {
            Assert.True(throttle.TryRecord(Guid.NewGuid(), Guid.NewGuid(), now));
        }

        Assert.Equal(
            KioskAnonymousSettingBudget.InstallationCapReached,
            throttle.Check(Guid.NewGuid(), Guid.NewGuid(), now));
    }
}
