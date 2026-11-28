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
        var nonce = await bed.Nonces.IssueAsync();
        var token = StubAppleValidator.Token("apple-sub-1");

        Assert.Equal(AppleSignInOutcome.SignedIn, (await bed.Service.SignInAsync(token, nonce, "Phone")).Outcome);
        Assert.Equal(AppleSignInOutcome.InvalidGrant, (await bed.Service.SignInAsync(token, nonce, "Phone")).Outcome);
        Assert.Equal(AppleSignInOutcome.InvalidGrant, (await bed.Service.SignInAsync(token, "never-issued", "Phone")).Outcome);
        Assert.Equal(AppleLinkOutcome.InvalidToken, await bed.Service.LinkAsync(user.Id, token, nonce));
    }

    [Fact]
    public async Task AnExpiredNonce_IsRefused()
    {
        using var bed = AppleSignInTestBed.Create();
        var clock = new MutableTestClock(DateTimeOffset.UtcNow);
        var nonces = new AppleNonceStore(bed.Contexts, clock);
        var nonce = await nonces.IssueAsync();

        clock.Advance(AppleNonceStore.Lifetime + TimeSpan.FromSeconds(1));

        Assert.False(await nonces.TryUseAsync(nonce));
    }

    [Fact]
    public async Task ABadTokenOrNoDevice_IsAnInvalidGrant()
    {
        using var bed = AppleSignInTestBed.Create();
        var user = await bed.AddUserAsync("climber");
        await bed.LinkAsync(user.Id, "apple-sub-1");

        Assert.Equal(AppleSignInOutcome.InvalidGrant, (await bed.Service.SignInAsync("forged", await bed.Nonces.IssueAsync(), "Phone")).Outcome);
        Assert.Equal(AppleSignInOutcome.InvalidGrant, (await bed.SignInAsync("apple-sub-1", device: " ​ ")).Outcome);
    }

    [Fact]
    public async Task AnAppleIdLinkedToAnotherAccount_IsRefused_AndRelinkingYourOwnIsANoOp()
    {
        using var bed = AppleSignInTestBed.Create();
        var first = await bed.AddUserAsync("first");
        var second = await bed.AddUserAsync("second");
        await bed.LinkAsync(first.Id, "apple-sub-1");

        Assert.Equal(AppleLinkOutcome.LinkedElsewhere, await bed.LinkAsync(second.Id, "apple-sub-1"));
        Assert.False(await bed.Service.IsLinkedAsync(second.Id));
        Assert.Equal(AppleLinkOutcome.Linked, await bed.LinkAsync(first.Id, "apple-sub-1"));
    }

    [Fact]
    public async Task LinkingAnotherAppleId_ReplacesTheOldOne()
    {
        using var bed = AppleSignInTestBed.Create();
        var user = await bed.AddUserAsync("climber");
        await bed.LinkAsync(user.Id, "apple-sub-1");

        Assert.Equal(AppleLinkOutcome.Linked, await bed.LinkAsync(user.Id, "apple-sub-2"));

        Assert.Equal(AppleSignInOutcome.NotLinked, (await bed.SignInAsync("apple-sub-1")).Outcome);
        Assert.Equal(AppleSignInOutcome.SignedIn, (await bed.SignInAsync("apple-sub-2")).Outcome);
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
