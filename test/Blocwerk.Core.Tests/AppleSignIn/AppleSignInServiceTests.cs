// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.AppleSignIn;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests.AppleSignIn;

/// <summary>
/// Linking, unlinking and signing in with an Apple ID: one account per Apple ID, nonces used once, a device key minted
/// like the device link's, and a <c>user.profile</c> feed row for the owner's devices on every link change.
/// </summary>
public sealed class AppleSignInServiceTests
{
    [Fact]
    public async Task Link_ThenSignIn_MintsADeviceKeyNamedAfterTheDevice()
    {
        using var bed = AppleSignInTestBed.Create();
        var user = await bed.AddUserAsync("climber");

        Assert.Equal(AppleLinkOutcome.Linked, await bed.LinkAsync(user.Id, "apple-sub-1"));
        Assert.True(await bed.Service.IsLinkedAsync(user.Id));

        var result = await bed.SignInAsync("apple-sub-1");

        Assert.Equal(AppleSignInOutcome.SignedIn, result.Outcome);
        Assert.Equal("iPhone: Test iPhone", result.Key!.Name);
        await using var db = bed.Harness.CreateContext();
        var key = await db.ApiKeys.IgnoreQueryFilters().SingleAsync(k => k.Id == result.Key.KeyId);
        Assert.True(key.IsDevice);
        Assert.Equal(user.Id, key.UserId);
        Assert.Equal(ApiKeyScope.User, key.Scope);
    }

    [Fact]
    public async Task SigningInTwiceFromTheSameDevice_RevokesTheFirstKey()
    {
        using var bed = AppleSignInTestBed.Create();
        var user = await bed.AddUserAsync("climber");
        await bed.LinkAsync(user.Id, "apple-sub-1");

        var first = await bed.SignInAsync("apple-sub-1");
        var second = await bed.SignInAsync("apple-sub-1");

        await using var db = bed.Harness.CreateContext();
        Assert.NotNull((await db.ApiKeys.IgnoreQueryFilters().SingleAsync(k => k.Id == first.Key!.KeyId)).RevokedAt);
        Assert.Null((await db.ApiKeys.IgnoreQueryFilters().SingleAsync(k => k.Id == second.Key!.KeyId)).RevokedAt);
    }

    [Fact]
    public async Task AnUnlinkedAppleId_IsNotLinked_AndMintsNothing()
    {
        using var bed = AppleSignInTestBed.Create();
        await bed.AddUserAsync("climber");

        Assert.Equal(AppleSignInOutcome.NotLinked, (await bed.SignInAsync("apple-sub-1")).Outcome);
        await using var db = bed.Harness.CreateContext();
        Assert.Empty(await db.ApiKeys.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task ADeletedAccount_IsNotLinked()
    {
        using var bed = AppleSignInTestBed.Create();
        var user = await bed.AddUserAsync("climber");
        await bed.LinkAsync(user.Id, "apple-sub-1");
        await using (var db = bed.Harness.CreateContext())
        {
            await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.DeletedAt, DateTimeOffset.UtcNow));
        }

        Assert.Equal(AppleSignInOutcome.NotLinked, (await bed.SignInAsync("apple-sub-1")).Outcome);
    }

    [Fact]
    public async Task ANonce_WorksOnce_AndOnlyWhenIssued()
    {
        using var bed = AppleSignInTestBed.Create();
        var user = await bed.AddUserAsync("climber");
        await bed.LinkAsync(user.Id, "apple-sub-1");
        var nonce = bed.Nonces.Issue();
        var token = StubAppleValidator.Token("apple-sub-1");

        Assert.Equal(AppleSignInOutcome.SignedIn, (await bed.Service.SignInAsync(token, nonce, "Phone")).Outcome);
        Assert.Equal(AppleSignInOutcome.InvalidGrant, (await bed.Service.SignInAsync(token, nonce, "Phone")).Outcome);
        Assert.Equal(AppleSignInOutcome.InvalidGrant, (await bed.Service.SignInAsync(token, "never-issued", "Phone")).Outcome);
    }

    [Fact]
    public async Task ABadToken_StillSpendsTheNonce()
    {
        using var bed = AppleSignInTestBed.Create();
        var user = await bed.AddUserAsync("climber");
        await bed.LinkAsync(user.Id, "apple-sub-1");
        var nonce = bed.Nonces.Issue();

        await bed.Service.SignInAsync("forged", nonce, "Phone");

        Assert.Equal(AppleSignInOutcome.InvalidGrant, (await bed.Service.SignInAsync(StubAppleValidator.Token("apple-sub-1"), nonce, "Phone")).Outcome);
    }

    [Fact]
    public async Task AnExpiredOrForgedNonce_IsRefused()
    {
        using var bed = AppleSignInTestBed.Create();
        var clock = new MutableTestClock(DateTimeOffset.UtcNow);
        var nonces = new AppleNonceStore(bed.Contexts, bed.Codec, clock);
        var nonce = nonces.Issue();
        var stranger = new AppleNonceStore(bed.Contexts, new Web.DeviceLinks.DataProtectionAppleNonceCodec(
            new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()));

        Assert.False(await stranger.TryUseAsync(nonce));
        Assert.False(await nonces.TryUseAsync(nonce[..^2] + "AA"));

        clock.Advance(AppleNonceStore.Lifetime + TimeSpan.FromSeconds(1));
        Assert.False(await nonces.TryUseAsync(nonce));
    }

    [Fact]
    public async Task IssuingANonce_StoresNothing()
    {
        using var bed = AppleSignInTestBed.Create();

        for (var i = 0; i < 5; i++)
        {
            bed.Nonces.Issue();
        }

        await using var db = bed.Harness.CreateContext();
        Assert.Empty(await db.AppleSignInNonces.ToListAsync());
    }

    [Fact]
    public async Task ABadTokenOrNoDevice_IsAnInvalidGrant()
    {
        using var bed = AppleSignInTestBed.Create();
        var user = await bed.AddUserAsync("climber");
        await bed.LinkAsync(user.Id, "apple-sub-1");

        Assert.Equal(AppleSignInOutcome.InvalidGrant, (await bed.Service.SignInAsync("forged", bed.Nonces.Issue(), "Phone")).Outcome);
        Assert.Equal(AppleSignInOutcome.InvalidGrant, (await bed.SignInAsync("apple-sub-1", device: " ​ ")).Outcome);
    }

    [Fact]
    public async Task AnAppleIdLinkedToAnotherAccount_IsRefused()
    {
        using var bed = AppleSignInTestBed.Create();
        var first = await bed.AddUserAsync("first");
        var second = await bed.AddUserAsync("second");
        await bed.LinkAsync(first.Id, "apple-sub-1");

        Assert.Equal(AppleLinkOutcome.LinkedElsewhere, await bed.LinkAsync(second.Id, "apple-sub-1"));
        Assert.False(await bed.Service.IsLinkedAsync(second.Id));
    }

    [Fact]
    public async Task ADevice_NeverReplacesAnAccountsAppleId()
    {
        using var bed = AppleSignInTestBed.Create();
        var user = await bed.AddUserAsync("climber");
        await bed.LinkAsync(user.Id, "apple-sub-1");

        Assert.Equal(AppleLinkOutcome.AlreadyLinked, await bed.LinkAsync(user.Id, "apple-sub-2"));
        Assert.Equal(AppleLinkOutcome.AlreadyLinked, await bed.LinkAsync(user.Id, "apple-sub-1"));

        Assert.Equal(AppleSignInOutcome.SignedIn, (await bed.SignInAsync("apple-sub-1")).Outcome);
        Assert.Equal(AppleSignInOutcome.NotLinked, (await bed.SignInAsync("apple-sub-2")).Outcome);
    }

    [Fact]
    public async Task AnOldOrRevokedOrForeignKey_CannotLink()
    {
        using var bed = AppleSignInTestBed.Create();
        var user = await bed.AddUserAsync("climber");
        var other = await bed.AddUserAsync("other");
        var old = await bed.DeviceKeyAsync(user.Id, "Old");
        var revoked = await bed.DeviceKeyAsync(user.Id, "Revoked");
        var foreign = await bed.DeviceKeyAsync(other.Id, "Theirs");
        await using (var db = bed.Harness.CreateContext())
        {
            var aged = DateTimeOffset.UtcNow - AppleSignInService.LinkKeyMaxAge - TimeSpan.FromMinutes(1);
            await db.ApiKeys.IgnoreQueryFilters().Where(k => k.Id == old).ExecuteUpdateAsync(s => s.SetProperty(k => k.CreatedAt, aged));
            await db.ApiKeys.IgnoreQueryFilters().Where(k => k.Id == revoked).ExecuteUpdateAsync(s => s.SetProperty(k => k.RevokedAt, DateTimeOffset.UtcNow));
        }

        foreach (var key in new[] { old, revoked, foreign })
        {
            Assert.Equal(AppleLinkOutcome.KeyNotFresh, await bed.Service.LinkAsync(user.Id, key, StubAppleValidator.Token("s"), bed.Nonces.Issue()));
        }

        Assert.False(await bed.Service.IsLinkedAsync(user.Id));
    }

    [Fact]
    public async Task TheLink_RecordsTheDeviceThatMadeIt()
    {
        using var bed = AppleSignInTestBed.Create();
        var user = await bed.AddUserAsync("climber");

        await bed.LinkAsync(user.Id, "apple-sub-1");

        var link = await bed.Service.FindLinkAsync(user.Id);
        Assert.Equal("iPhone: Linking iPhone", link?.LinkedFrom);
    }

    [Fact]
    public async Task APasswordReset_DropsTheAppleId()
    {
        using var bed = AppleSignInTestBed.Create();
        var user = await bed.AddUserAsync("climber");
        await bed.LinkAsync(user.Id, "apple-sub-1");

        await new PasswordLoginService(bed.Contexts, new PasswordService()).ResetPasswordAsync(user.Id, "a-new-password-1");

        Assert.False(await bed.Service.IsLinkedAsync(user.Id));
    }

    [Fact]
    public async Task SettingAFirstPassword_KeepsTheAppleId_ButChangingItDropsIt()
    {
        using var bed = AppleSignInTestBed.Create();
        var user = await bed.AddUserAsync("climber");
        await bed.LinkAsync(user.Id, "apple-sub-1");
        var passwords = new PasswordLoginService(bed.Contexts, new PasswordService());

        await passwords.SetPasswordAsync(user.Id, "climber", "first-password-1", null);
        Assert.True(await bed.Service.IsLinkedAsync(user.Id));

        await passwords.SetPasswordAsync(user.Id, "climber", "second-password-2", "first-password-1");
        Assert.False(await bed.Service.IsLinkedAsync(user.Id));
    }

    [Fact]
    public async Task Unlink_StopsTheSignIn_AndUnlinkingNoneIsFalse()
    {
        using var bed = AppleSignInTestBed.Create();
        var user = await bed.AddUserAsync("climber");
        await bed.LinkAsync(user.Id, "apple-sub-1");

        Assert.True(await bed.Service.UnlinkAsync(user.Id));
        Assert.False(await bed.Service.UnlinkAsync(user.Id));
        Assert.Equal(AppleSignInOutcome.NotLinked, (await bed.SignInAsync("apple-sub-1")).Outcome);
    }

    [Fact]
    public async Task Unlink_IsRefused_WhileTheAppleIdIsTheOnlyWayIn()
    {
        using var bed = AppleSignInTestBed.Create();
        var user = await bed.AddAppleOnlyUserAsync("apple-only");
        await bed.LinkAsync(user.Id, "apple-sub-1");

        Assert.False(await bed.Service.HasOtherSignInAsync(user.Id));
        await Assert.ThrowsAsync<AppleOnlySignInException>(() => bed.Service.UnlinkAsync(user.Id));
        Assert.True(await bed.Service.IsLinkedAsync(user.Id));

        await using (var db = bed.Harness.CreateContext())
        {
            (await db.Users.SingleAsync(u => u.Id == user.Id)).PasswordHash = "hash";
            await db.SaveChangesAsync();
        }

        Assert.True(await bed.Service.UnlinkAsync(user.Id));
    }

    [Fact]
    public async Task LinkAndUnlink_TellTheOwnersDevices()
    {
        using var bed = AppleSignInTestBed.Create();
        var user = await bed.AddUserAsync("climber");

        await bed.LinkAsync(user.Id, "apple-sub-1");
        await bed.Service.UnlinkAsync(user.Id);

        await using var db = bed.Harness.CreateContext();
        var rows = await db.WallChanges.AsNoTracking().Where(c => c.Kind == FeedKinds.UserProfile).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(FeedAudience.User, r.Audience);
            Assert.Equal(user.Id, r.AudienceUserId);
            Assert.Null(r.WallId);
        });
    }

    [Fact]
    public async Task AKioskSession_CannotSignIn()
    {
        using var bed = AppleSignInTestBed.Create();
        var user = await bed.AddUserAsync("climber");
        await bed.LinkAsync(user.Id, "apple-sub-1");
        bed.Kiosk.IsKiosk.Returns(true);

        await Assert.ThrowsAsync<KioskRestrictedException>(() => bed.SignInAsync("apple-sub-1"));
    }
}
