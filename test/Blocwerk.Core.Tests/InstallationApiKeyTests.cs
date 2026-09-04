using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The installation-scoped key: the widest key the app can mint, and the only one that belongs to
/// the server rather than to a wall or a person. These tests pin who may create it (an app
/// administrator, decided against the database), who may retire it, and that a kiosk tablet cannot
/// produce one however much authority the person standing at it holds.
/// </summary>
public class InstallationApiKeyTests
{
    [Fact]
    public async Task CreateInstallationKey_RequiresAnAppAdmin()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        // The wall's OWNER is not automatically an installation administrator: wall authority and
        // app authority are different things, and this key answers to the second one.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => h.ApiKeyService.CreateInstallationKeyAsync(h.Owner.Id, "Deploy hook", null));

        await MakeAdminAsync(h, h.Owner.Id);

        var (key, token) = await h.ApiKeyService.CreateInstallationKeyAsync(h.Owner.Id, "Deploy hook", null);

        Assert.Equal(ApiKeyScope.Installation, key.Scope);
        Assert.Null(key.WallId);

        // The minter is recorded so the key is traceable to a person — not so it inherits them.
        Assert.Equal(h.Owner.Id, key.UserId);

        var validated = await h.ApiKeyService.ValidateAsync(token);
        Assert.Equal(ApiKeyScope.Installation, validated!.Scope);

        // ...and it is emphatically not a kiosk key.
        Assert.Null(await h.ApiKeyService.ValidateKioskAsync(token));
    }

    [Fact]
    public async Task CreateInstallationKey_IsRefusedForADeletedAdminAndForNobody()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        await MakeAdminAsync(h, h.Owner.Id);

        await using (var db = h.CreateContext())
        {
            var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == h.Owner.Id);
            user.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => h.ApiKeyService.CreateInstallationKeyAsync(h.Owner.Id, "Deploy hook", null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => h.ApiKeyService.CreateInstallationKeyAsync(Guid.Empty, "Deploy hook", null));
    }

    [Fact]
    public async Task ListingAndRevoking_AnInstallationKey_AreAdminOnly()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        await MakeAdminAsync(h, h.Owner.Id);

        var (key, _) = await h.ApiKeyService.CreateInstallationKeyAsync(h.Owner.Id, "Deploy hook", null);
        var stranger = await SeedUserAsync(h, "stranger@test");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => h.ApiKeyService.GetInstallationKeysAsync(stranger));

        // Not even the key's own owner field is the gate — a demoted minter loses the key with the
        // role, and any other administrator can retire it.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => h.ApiKeyService.RevokeAsync(key.Id, stranger));

        await MakeAdminAsync(h, stranger);
        await h.ApiKeyService.RevokeAsync(key.Id, stranger);

        var listed = await h.ApiKeyService.GetInstallationKeysAsync(h.Owner.Id);
        Assert.Single(listed);
        Assert.NotNull(listed[0].RevokedAt);
    }

    [Fact]
    public async Task InstallationKeys_AreNotListedAsWallOrUserKeys()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        await MakeAdminAsync(h, h.Owner.Id);

        await h.ApiKeyService.CreateInstallationKeyAsync(h.Owner.Id, "Deploy hook", null);

        // Its owner column names the admin, but it is not one of that admin's personal keys.
        Assert.Empty(await h.ApiKeyService.GetUserKeysAsync(h.Owner.Id, h.Owner.Id));
        Assert.Empty(await h.ApiKeyService.GetWallKeysAsync(h.WallId, h.Owner.Id));
    }

    [Fact]
    public async Task CreateInstallationKey_IsRefusedFromAKioskSession()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        await MakeAdminAsync(h, h.Owner.Id);

        var kiosk = Substitute.For<IKioskContext>();
        kiosk.IsKiosk.Returns(true);
        kiosk.KioskWallId.Returns(h.WallId);
        var guarded = new KioskGuardedApiKeyService(h.ApiKeyService, kiosk);

        await Assert.ThrowsAsync<KioskRestrictedException>(
            () => guarded.CreateInstallationKeyAsync(h.Owner.Id, "Deploy hook", null));

        // Listing is refused too — installation keys are the app-admin surface, not the wall's, and
        // BlocwerkPolicies.AppAdmin already refuses a kiosk session in front of it. This assertion
        // used to say Assert.Empty: the service was the weaker of the two, which is the divergence
        // this test now pins shut.
        await Assert.ThrowsAsync<KioskRestrictedException>(
            () => guarded.GetInstallationKeysAsync(h.Owner.Id));

        var ordinary = Substitute.For<IKioskContext>();
        ordinary.IsKiosk.Returns(false);
        var unguarded = new KioskGuardedApiKeyService(h.ApiKeyService, ordinary);

        var (key, _) = await unguarded.CreateInstallationKeyAsync(h.Owner.Id, "Deploy hook", null);
        Assert.Equal(ApiKeyScope.Installation, key.Scope);
    }

    /// <summary>
    /// The other half of the kiosk guard, and the one with teeth: an app admin standing at a gym
    /// tablet must not be able to retire the deploy hook's key. Revoking is de-escalation and is
    /// deliberately left open for every OTHER scope, so this pins the asymmetry rather than just
    /// the refusal — a blanket "revoke is blocked on kiosk" would break switching off the very
    /// tablet you are standing at.
    /// </summary>
    [Fact]
    public async Task RevokingAnInstallationKey_IsRefusedFromAKiosk_ButWallKeysStillRevoke()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        await MakeAdminAsync(h, h.Owner.Id);

        var (installationKey, _) = await h.ApiKeyService.CreateInstallationKeyAsync(h.Owner.Id, "Deploy hook", null);
        var (wallKey, _) = await h.ApiKeyService.CreateWallKeyAsync(h.WallId, h.Owner.Id, "Sensor", null);

        var kiosk = Substitute.For<IKioskContext>();
        kiosk.IsKiosk.Returns(true);
        kiosk.KioskWallId.Returns(h.WallId);
        var guarded = new KioskGuardedApiKeyService(h.ApiKeyService, kiosk);

        await Assert.ThrowsAsync<KioskRestrictedException>(
            () => guarded.RevokeAsync(installationKey.Id, h.Owner.Id));

        // ...and it really was not revoked behind the exception.
        var stillLive = await h.ApiKeyService.GetInstallationKeysAsync(h.Owner.Id);
        Assert.Null(Assert.Single(stillLive).RevokedAt);

        // The de-escalation this guard must NOT break: retiring a wall key from the tablet.
        await guarded.RevokeAsync(wallKey.Id, h.Owner.Id);
        var wallKeys = await h.ApiKeyService.GetWallKeysAsync(h.WallId, h.Owner.Id);
        Assert.NotNull(Assert.Single(wallKeys, k => k.Id == wallKey.Id).RevokedAt);
    }

    /// <summary>
    /// A non-admin at a kiosk gets the same refusal they always did — an authorization failure, not
    /// a kiosk one. The guard must not accidentally UPGRADE a stranger's outcome by answering
    /// "kiosk" before the inner service has had its say about authority.
    /// </summary>
    [Fact]
    public async Task RevokeFromAKiosk_ByANonAdmin_StillFailsAuthorization()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        await MakeAdminAsync(h, h.Owner.Id);

        var (installationKey, _) = await h.ApiKeyService.CreateInstallationKeyAsync(h.Owner.Id, "Deploy hook", null);
        var stranger = await SeedUserAsync(h, "stranger-kiosk@test");

        var kiosk = Substitute.For<IKioskContext>();
        kiosk.IsKiosk.Returns(true);
        kiosk.KioskWallId.Returns(h.WallId);
        var guarded = new KioskGuardedApiKeyService(h.ApiKeyService, kiosk);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => guarded.RevokeAsync(installationKey.Id, stranger));
    }

    private static async Task MakeAdminAsync(WallTestHarness h, Guid userId)
    {
        await using var db = h.CreateContext();
        var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId);
        user.Role = IdentityRole.Admin;
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> SeedUserAsync(WallTestHarness h, string identifier)
    {
        await using var db = h.CreateContext();
        var user = new User { Identifier = identifier, DisplayName = identifier };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }
}
