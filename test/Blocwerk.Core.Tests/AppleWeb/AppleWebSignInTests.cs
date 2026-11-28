// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.AppleSignIn;
using Blocwerk.Core.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests.AppleWeb;

/// <summary>
/// Web Sign in with Apple against a stubbed Apple: the round trip verifies the token for the Services ID and the
/// round trip's own nonce, an unknown <c>sub</c> opens a new account like the other providers, and accounts are found
/// by the linked <c>sub</c> only, never by e-mail.
/// </summary>
public sealed class AppleWebSignInTests : IDisposable
{
    private const string Relay = "x7k2@privaterelay.appleid.com";

    private readonly AppleWebHarness harness = new();

    [Fact]
    public async Task TheRoundTrip_GivesTheSub_AndCarriesTheFirstAuthorizationName()
    {
        var (subject, ticket) = await harness.RoundTripAsync("001.apple.sub", """{"name":{"firstName":"Ada","lastName":"Lovelace"}}""");

        Assert.Equal("001.apple.sub", subject);
        Assert.Equal("Ada Lovelace", ticket.Name);
    }

    [Fact]
    public async Task ATokenForTheIosApp_OrAnotherNonce_OrARefusedCode_GivesNoSub()
    {
        var (forTheApp, _) = await harness.RoundTripAsync("001.apple.sub", audience: AppleSignInSettings.IosBundleId);
        Assert.Null(forTheApp);

        var start = new DefaultHttpContext();
        harness.Web.BeginAuthorize(start.Response, "state-1");
        var post = new DefaultHttpContext();
        post.Request.Headers.Cookie = AppleWebHarness.CookieHeader(start.Response);
        Assert.True(harness.Web.AcceptCallback(post.Request, post.Response, "state-1", "code-1", null));
        Assert.True(harness.Web.TryTakeTicket("code-1", out var ticket));
        harness.NextIdToken = harness.Apple.IdToken("001.apple.sub", "a nonce from another round trip");
        Assert.Null(await harness.Web.VerifyAsync("code-1", ticket, AppleWebHarness.RedirectUri));

        harness.NextIdToken = null;
        Assert.Null(await harness.Web.VerifyAsync("code-1", ticket, AppleWebHarness.RedirectUri));
    }

    [Fact]
    public async Task ACallback_WithoutTheBrowsersCookie_OrForAnotherState_ParksNothing()
    {
        var start = new DefaultHttpContext();
        harness.Web.BeginAuthorize(start.Response, "state-1");

        var foreign = new DefaultHttpContext();
        Assert.False(harness.Web.AcceptCallback(foreign.Request, foreign.Response, "state-1", "code-1", null));

        var mixed = new DefaultHttpContext();
        mixed.Request.Headers.Cookie = AppleWebHarness.CookieHeader(start.Response);
        Assert.False(harness.Web.AcceptCallback(mixed.Request, mixed.Response, "state-2", "code-1", null));
        Assert.False(harness.Web.TryTakeTicket("code-1", out _));
    }

    [Fact]
    public async Task AnUnknownSub_OpensANewAccount_NamedByApple_AndTheNextSignInFindsIt()
    {
        var created = await harness.Web.FindOrCreateUserAsync("001.new", "Ada Lovelace");
        var again = await harness.Web.FindOrCreateUserAsync("001.new", null);

        Assert.NotNull(created);
        Assert.Equal("Ada Lovelace", created.DisplayName);
        Assert.Equal(created.Id, again!.Id);
        await using var db = harness.Db.CreateDbContext();
        Assert.Equal(created.Id, (await db.AppleLinks.SingleAsync(l => l.Subject == "001.new")).UserId);
        Assert.Equal("Ada Lovelace", (await db.Users.SingleAsync(u => u.Id == created.Id)).DisplayName);
    }

    [Fact]
    public async Task WithoutAName_TheNewAccountIsAnAppleUser()
    {
        var created = await harness.Web.FindOrCreateUserAsync("001.nameless", null);

        Assert.Equal("Apple user", created!.DisplayName);
    }

    [Fact]
    public async Task AnAccountWithTheSameEmail_IsNeverMerged_TheAppleIdOpensItsOwnAccount()
    {
        var existing = await AddUserAsync("Existing", Relay);

        var (subject, _) = await harness.RoundTripAsync("001.collides", email: Relay);
        var signedIn = await harness.Web.FindOrCreateUserAsync(subject!, null);

        Assert.NotEqual(existing.Id, signedIn!.Id);
        Assert.False(await harness.Links.IsLinkedAsync(existing.Id));
        await using var db = harness.Db.CreateDbContext();
        Assert.Null((await db.Users.SingleAsync(u => u.Id == signedIn.Id)).Email);
        Assert.Equal(Relay, (await db.Users.SingleAsync(u => u.Id == existing.Id)).Email);
    }

    [Fact]
    public async Task Linking_AttachesTheSub_ThenSignInOpensThatAccount_AndUnlinkingEndsIt()
    {
        var user = await AddUserAsync("Linker", null, passwordHash: "hash");

        Assert.Equal(AppleLinkOutcome.Linked, await harness.Web.LinkAsync(user.Id, "001.linked"));
        Assert.Equal(AppleLinkOutcome.Linked, await harness.Web.LinkAsync(user.Id, "001.linked"));
        Assert.True(await harness.Web.IsLinkedToAsync(user.Id, "001.linked"));
        Assert.Equal(user.Id, (await harness.Web.FindOrCreateUserAsync("001.linked", "Someone Else"))!.Id);

        Assert.True(await harness.Links.UnlinkAsync(user.Id));
        Assert.False(await harness.Web.IsLinkedToAsync(user.Id, "001.linked"));
        Assert.NotEqual(user.Id, (await harness.Web.FindOrCreateUserAsync("001.linked", null))!.Id);
    }

    [Fact]
    public async Task ASubLinkedElsewhere_IsRefused_AndNeitherAccountIsMerged()
    {
        var holder = await AddUserAsync("Holder", null);
        var other = await AddUserAsync("Other", null);
        await harness.Web.LinkAsync(holder.Id, "001.taken");

        Assert.Equal(AppleLinkOutcome.LinkedElsewhere, await harness.Web.LinkAsync(other.Id, "001.taken"));
        Assert.False(await harness.Web.IsLinkedToAsync(other.Id, "001.taken"));
        await using var db = harness.Db.CreateDbContext();
        Assert.Equal(2, await db.Users.CountAsync(u => u.Id == holder.Id || u.Id == other.Id));
    }

    [Fact]
    public async Task ASubLinkedToADeletedAccount_SignsNobodyIn()
    {
        var gone = await AddUserAsync("Gone", null);
        await harness.Web.LinkAsync(gone.Id, "001.gone");
        await using (var db = harness.Db.CreateDbContext())
        {
            var row = await db.Users.SingleAsync(u => u.Id == gone.Id);
            row.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        Assert.Null(await harness.Web.FindOrCreateUserAsync("001.gone", null));
    }

    [Fact]
    public async Task AnAccountMadeByApple_HasNoOtherWayIn_UntilAPasswordOrProviderIsAdded()
    {
        var appleOnly = await harness.Web.FindOrCreateUserAsync("001.only", null);
        Assert.False(await harness.Links.HasOtherSignInAsync(appleOnly!.Id));

        await using (var db = harness.Db.CreateDbContext())
        {
            db.UserIdentities.Add(new UserIdentity { UserId = appleOnly.Id, Provider = "github", ProviderUserId = "gh-1" });
            await db.SaveChangesAsync();
        }

        Assert.True(await harness.Links.HasOtherSignInAsync(appleOnly.Id));
        var withPassword = await AddUserAsync("Pw", null);
        await using (var db = harness.Db.CreateDbContext())
        {
            (await db.Users.SingleAsync(u => u.Id == withPassword.Id)).PasswordHash = "hash";
            await db.SaveChangesAsync();
        }

        Assert.True(await harness.Links.HasOtherSignInAsync(withPassword.Id));
    }

    [Fact]
    public async Task ACredentialReset_KeepsTheAppleIdTheAccountWasMadeWith_AndDropsALaterLink()
    {
        var madeByApple = await harness.Web.FindOrCreateUserAsync("001.origin", "Ada");
        var linkedLater = await AddUserAsync("Later", null, passwordHash: "hash");
        await harness.Web.LinkAsync(linkedLater.Id, "001.later");

        await using (var db = harness.Db.CreateDbContext())
        {
            await AppleLinkReset.StageAsync(db, madeByApple!.Id);
            await AppleLinkReset.StageAsync(db, linkedLater.Id);
            await db.SaveChangesAsync();
        }

        Assert.True(await harness.Web.IsLinkedToAsync(madeByApple.Id, "001.origin"));
        Assert.False(await harness.Links.IsLinkedAsync(linkedLater.Id));
    }

    public void Dispose() => harness.Dispose();

    private async Task<User> AddUserAsync(string name, string? email, string? passwordHash = null)
    {
        await using var db = harness.Db.CreateDbContext();
        var user = new User
        {
            Identifier = $"{name}__gh|{Guid.NewGuid():N}",
            DisplayName = name,
            Email = email,
            PasswordHash = passwordHash,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }
}
