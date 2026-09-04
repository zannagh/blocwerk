using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The per-TABLET half of the anonymous-setting gate: <see cref="ApiKey.AllowAnonymousKioskSetting"/>.
/// </summary>
/// <remarks>
/// The wall's flag is the master switch and the key's flag can only narrow it, so the two are pinned
/// here as a truth table — all four combinations, each asserting what was written rather than merely
/// that nothing threw. The flag lives on the ApiKey row and is read fresh on every attempt, so it
/// also has to bite a tablet that is ALREADY registered, without re-pairing it. The UI predicate
/// (<c>IBoulderService.CanCreateAnonymouslyAsync</c>) and the kiosk guard on the setter are pinned
/// here too, so page and gate cannot drift apart.
/// </remarks>
public class KioskAnonymousKeySettingTests
{
    [Fact]
    public async Task WithBothTheWallAndTheKeyOn_TheCreateIsAllowed()
    {
        using var fixture = await AnonymousSettingFixture.CreateAsync();
        await fixture.AllowAnonymousSettingAsync();
        await fixture.SetKeyAnonymousSettingAsync(true);

        var boulders = fixture.BoulderServiceFor(fixture.LiveKioskContext());
        await fixture.CreateAsync(boulders, name: "Both On");

        var stored = await fixture.StoredBouldersAsync();
        Assert.Equal("Both On", Assert.Single(stored).Name);
    }

    [Fact]
    public async Task WithTheWallOnButTheKeyOff_TheCreateIsRefusedAndNothingIsWritten()
    {
        using var fixture = await AnonymousSettingFixture.CreateAsync();
        await fixture.AllowAnonymousSettingAsync();
        await fixture.SetKeyAnonymousSettingAsync(false);

        // The opt-OUT case the flag exists for: one excluded tablet in a gym that otherwise allows it.
        var boulders = fixture.BoulderServiceFor(fixture.LiveKioskContext());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.CreateAsync(boulders));
        Assert.Empty(await fixture.StoredBouldersAsync());
    }

    [Fact]
    public async Task WithTheWallOffButTheKeyOn_TheCreateIsRefusedAndNothingIsWritten()
    {
        using var fixture = await AnonymousSettingFixture.CreateAsync();
        await fixture.SetKeyAnonymousSettingAsync(true);

        // The key flag NARROWS; it can never grant on its own. A gym that never switched the wall on
        // has no unauthenticated write surface, whatever its keys say.
        var boulders = fixture.BoulderServiceFor(fixture.LiveKioskContext());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.CreateAsync(boulders));
        Assert.Empty(await fixture.StoredBouldersAsync());
    }

    [Fact]
    public async Task WithBothTheWallAndTheKeyOff_TheCreateIsRefusedAndNothingIsWritten()
    {
        using var fixture = await AnonymousSettingFixture.CreateAsync();
        await fixture.SetKeyAnonymousSettingAsync(false);

        var boulders = fixture.BoulderServiceFor(fixture.LiveKioskContext());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.CreateAsync(boulders));
        Assert.Empty(await fixture.StoredBouldersAsync());
    }

    /// <summary>
    /// The migration backfills <c>true</c>, and a freshly minted key arrives the same way — the wall
    /// flag alone decided this before the column existed, and it still does until somebody opts a
    /// tablet out.
    /// </summary>
    [Fact]
    public async Task AKeyIsAllowedByDefault_OnTheEntityAndInTheDatabase()
    {
        Assert.True(new ApiKey { Name = "x", KeyHash = "h", Prefix = "bwk_" }.AllowAnonymousKioskSetting);

        using var fixture = await AnonymousSettingFixture.CreateAsync();
        Assert.True(fixture.KioskKey.AllowAnonymousKioskSetting);

        await using var db = fixture.Harness.CreateContext();
        var stored = await db.ApiKeys
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstAsync(k => k.Id == fixture.KioskKey.Id);
        Assert.True(stored.AllowAnonymousKioskSetting);

        // And nothing else had to happen for it to work: the wall's switch is the only thing between
        // this untouched key and an allowed create.
        await fixture.AllowAnonymousSettingAsync();
        await fixture.CreateAsync(fixture.BoulderServiceFor(fixture.LiveKioskContext()));
        Assert.Single(await fixture.StoredBouldersAsync());
    }

    /// <summary>
    /// It applies LIVE to an already-registered tablet: the device cookie is untouched throughout,
    /// so only the freshly read ApiKey row can be the difference.
    /// </summary>
    [Fact]
    public async Task OptingAKeyOut_StopsAnAlreadyRegisteredTabletWithoutRePairing()
    {
        using var fixture = await AnonymousSettingFixture.CreateAsync();
        await fixture.AllowAnonymousSettingAsync();

        var boulders = fixture.BoulderServiceFor(fixture.LiveKioskContext());
        await fixture.CreateAsync(boulders, name: "Before Opt Out");
        Assert.Single(await fixture.StoredBouldersAsync());

        await fixture.Harness.ApiKeyService.SetAnonymousKioskSettingAsync(
            fixture.KioskKey.Id, fixture.Harness.Owner.Id, false);

        // Same service instance, i.e. the same circuit and the same cached key validity.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => fixture.CreateAsync(boulders, name: "After Opt Out"));
        Assert.Single(await fixture.StoredBouldersAsync());

        // And back on again, so the refusal above is the flag and nothing incidental.
        await fixture.Harness.ApiKeyService.SetAnonymousKioskSettingAsync(
            fixture.KioskKey.Id, fixture.Harness.Owner.Id, true);
        await fixture.CreateAsync(boulders, name: "After Opt In");
        Assert.Equal(2, (await fixture.StoredBouldersAsync()).Count);
    }

    /// <summary>
    /// Same authority as revoking the key: the wall's admins govern the wall's keys. An ordinary
    /// member is refused, and the flag is left exactly as it was.
    /// </summary>
    [Fact]
    public async Task ANonAdminMayNotChangeTheKeyFlag()
    {
        using var fixture = await AnonymousSettingFixture.CreateAsync();
        var member = await fixture.Harness.AddMemberAsync("plain-member", WallRole.Member);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => fixture.Harness.ApiKeyService.SetAnonymousKioskSettingAsync(
                fixture.KioskKey.Id, member.Id, false));

        await using var db = fixture.Harness.CreateContext();
        var stored = await db.ApiKeys
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstAsync(k => k.Id == fixture.KioskKey.Id);
        Assert.True(stored.AllowAnonymousKioskSetting);
    }

    /// <summary>A wall key has no tablet, so the flag means nothing on it and the call is refused.</summary>
    [Fact]
    public async Task TheFlagCannotBeSetOnANonKioskKey()
    {
        using var fixture = await AnonymousSettingFixture.CreateAsync();
        var (wallKey, _) = await fixture.Harness.ApiKeyService.CreateWallKeyAsync(
            fixture.Harness.WallId, fixture.Harness.Owner.Id, "Sensor", null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Harness.ApiKeyService.SetAnonymousKioskSettingAsync(
                wallKey.Id, fixture.Harness.Owner.Id, false));
    }

    /// <summary>
    /// The predicate the pages ask before they offer a create at all — it has to answer the same
    /// thing the gate does, per-key half included, or a tablet gets the whole hold picker and a raw
    /// refusal at Publish.
    /// </summary>
    [Fact]
    public async Task CanCreateAnonymously_FollowsBothFlags()
    {
        using var fixture = await AnonymousSettingFixture.CreateAsync();
        await fixture.AllowAnonymousSettingAsync();

        var boulders = fixture.BoulderServiceFor(fixture.LiveKioskContext());
        Assert.True(await boulders.CanCreateAnonymouslyAsync(fixture.Harness.WallId));

        await fixture.SetKeyAnonymousSettingAsync(false);
        Assert.False(await boulders.CanCreateAnonymouslyAsync(fixture.Harness.WallId));

        // And the answer really is the gate's: the create it was predicting is refused too.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.CreateAsync(boulders));
    }

    /// <summary>
    /// It is a PERMISSION predicate and nothing more: it must not consume the volume budget the
    /// create also checks, so calling it repeatedly — a page may, on every render — cannot be what
    /// exhausts a tablet's allowance.
    /// </summary>
    [Fact]
    public async Task CanCreateAnonymously_DoesNotSpendTheThrottleBudget()
    {
        using var fixture = await AnonymousSettingFixture.CreateAsync();
        await fixture.AllowAnonymousSettingAsync();

        var boulders = fixture.BoulderServiceFor(fixture.LiveKioskContext());
        for (var i = 0; i < 50; i++)
        {
            Assert.True(await boulders.CanCreateAnonymouslyAsync(fixture.Harness.WallId));
        }

        await fixture.CreateAsync(boulders, name: "Still Within Budget");
        Assert.Single(await fixture.StoredBouldersAsync());
    }

    /// <summary>
    /// The kiosk guard is ASYMMETRIC on purpose: a tablet must not be able to switch its own
    /// anonymous setting ON (that is self-escalation, guarded like minting), but switching it OFF
    /// only takes permission away, so an admin standing at the tablet can do it there.
    /// </summary>
    [Fact]
    public async Task FromAKioskSession_TurningTheFlagOnIsRefusedButTurningItOffIsAllowed()
    {
        using var fixture = await AnonymousSettingFixture.CreateAsync();
        var kiosk = AnonymousSettingFixture.KioskContextFor(
            isKiosk: true, fixture.Harness.WallId, fixture.KioskKey.Id);
        var guarded = new KioskGuardedApiKeyService(fixture.Harness.ApiKeyService, kiosk);

        await Assert.ThrowsAsync<KioskRestrictedException>(
            () => guarded.SetAnonymousKioskSettingAsync(
                fixture.KioskKey.Id, fixture.Harness.Owner.Id, true));
        Assert.True(await StoredFlagAsync(fixture));

        // De-escalation goes through, and it really lands in the database.
        await guarded.SetAnonymousKioskSettingAsync(
            fixture.KioskKey.Id, fixture.Harness.Owner.Id, false);
        Assert.False(await StoredFlagAsync(fixture));

        // Which the tablet then cannot undo by itself — the one direction the guard exists for.
        await Assert.ThrowsAsync<KioskRestrictedException>(
            () => guarded.SetAnonymousKioskSettingAsync(
                fixture.KioskKey.Id, fixture.Harness.Owner.Id, true));
        Assert.False(await StoredFlagAsync(fixture));
    }

    private static async Task<bool> StoredFlagAsync(AnonymousSettingFixture fixture)
    {
        await using var db = fixture.Harness.CreateContext();
        var key = await db.ApiKeys
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstAsync(k => k.Id == fixture.KioskKey.Id);
        return key.AllowAnonymousKioskSetting;
    }
}
