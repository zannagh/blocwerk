using System.Security.Claims;
using Blocwerk.Authentication.Services;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A session signed in with an API key is marked, bounded by the key, re-checked against it, and
/// refused every account-security action.
/// </summary>
public class ApiKeySessionTests
{
    [Fact]
    public void Properties_CapTheLifetimeAtEightHours_AndAtTheKeysExpiry_WithoutSliding()
    {
        var now = DateTimeOffset.UtcNow;

        var open = ApiKeySessionSignIn.BuildProperties(Key(expiresAt: null), now);
        var shortLived = ApiKeySessionSignIn.BuildProperties(Key(expiresAt: now.AddHours(1)), now);

        // AuthenticationProperties stores instants to the second.
        Assert.Equal(now.AddHours(8), open.ExpiresUtc!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(now.AddHours(1), shortLived.ExpiresUtc!.Value, TimeSpan.FromSeconds(1));
        Assert.False(open.AllowRefresh);
        Assert.False(open.IsPersistent);
        Assert.False(ApiKeySessionSignIn.IsRecheckDue(open, now));
        Assert.True(ApiKeySessionSignIn.IsRecheckDue(open, now.AddMinutes(6)));
    }

    [Fact]
    public async Task RevokingTheKey_EndsTheSession_OnTheNextDueValidation()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var key = await StoreKeyAsync(h);
        var context = ValidationContext(h, key, checkedAt: DateTimeOffset.UtcNow.AddMinutes(-10));

        await ApiKeySessionValidator.ValidateAsync(context);
        Assert.NotNull(context.Principal);
        Assert.True(context.ShouldRenew);

        await using (var db = h.CreateContext())
        {
            db.ApiKeys.Single(k => k.Id == key.Id).RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var after = ValidationContext(h, key, checkedAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        await ApiKeySessionValidator.ValidateAsync(after);

        Assert.Null(after.Principal);
    }

    [Fact]
    public async Task AnOrdinarySession_IsNeverTouched()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var context = ValidationContext(h, key: null, checkedAt: null);

        await ApiKeySessionValidator.ValidateAsync(context);

        Assert.NotNull(context.Principal);
        Assert.False(context.ShouldRenew);
    }

    [Fact]
    public void Principal_CarriesTheMarkerClaims()
    {
        var user = new User { Identifier = "Climber__gh|1", DisplayName = "Climber" };
        var key = Key(expiresAt: null);

        var principal = ApiKeySessionSignIn.BuildPrincipal(user, key);

        Assert.True(ApiKeySessionClaims.IsApiKeySession(principal));
        Assert.Equal("apikey", principal.FindFirst("amr")!.Value);
        Assert.Equal(key.Id.ToString(), principal.FindFirst("api_key_id")!.Value);
        Assert.Equal(user.Id.ToString(), principal.FindFirst("uid")!.Value);
        Assert.False(ApiKeySessionClaims.IsApiKeySession(UserCookieSignIn.BuildPrincipal(user)));
    }

    [Fact]
    public async Task SensitiveAccountActions_AreRefusedInAKeySession()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var session = Substitute.For<IApiKeySessionContext>();
        session.IsApiKeySession.Returns(true);
        var kiosk = Substitute.For<IKioskContext>();

        var keys = new KioskGuardedApiKeyService(h.ApiKeyService, kiosk, session);
        var mint = await Assert.ThrowsAsync<ApiKeySessionRestrictedException>(
            () => keys.CreateUserKeyAsync(h.Owner.Id, h.Owner.Id, "k", null));
        await Assert.ThrowsAsync<ApiKeySessionRestrictedException>(() => keys.RevokeAsync(Guid.NewGuid(), h.Owner.Id));
        Assert.Equal("Not available in a session signed in with an API key.", mint.Message);

        var currentUser = new CurrentUserService(
            new BlocwerkSettings(),
            h.DbContextFactory,
            Substitute.For<IPasswordLoginService>(),
            Substitute.For<ITotpService>(),
            apiKeySession: session);
        await Assert.ThrowsAsync<ApiKeySessionRestrictedException>(() => currentUser.SetPasswordAsync("me", "password1", null));
        await Assert.ThrowsAsync<ApiKeySessionRestrictedException>(() => currentUser.BeginTotpEnrollmentAsync());
        await Assert.ThrowsAsync<ApiKeySessionRestrictedException>(() => currentUser.DisableTotpAsync("123456"));

        var deletion = new AccountDeletionService(
            h.DbContextFactory,
            Substitute.For<IBetaVideoStorage>(),
            h.CurrentUser,
            NullLogger<AccountDeletionService>.Instance,
            apiKeySession: session);
        await Assert.ThrowsAsync<ApiKeySessionRestrictedException>(() => deletion.DeleteAsync(h.Owner.Id));
        await Assert.ThrowsAsync<ApiKeySessionRestrictedException>(() => deletion.PreviewAsync(h.Owner.Id));
    }

    [Fact]
    public void SessionContext_ReadsTheMarkerOffTheRequest()
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        var user = new User { Identifier = "Climber__gh|1", DisplayName = "Climber" };
        accessor.HttpContext.Returns(new DefaultHttpContext { User = ApiKeySessionSignIn.BuildPrincipal(user, Key(null)) });

        Assert.True(new ApiKeySessionContext(accessor).IsApiKeySession);

        accessor.HttpContext.Returns(new DefaultHttpContext { User = UserCookieSignIn.BuildPrincipal(user) });
        Assert.False(new ApiKeySessionContext(accessor).IsApiKeySession);
    }

    private static ApiKey Key(DateTimeOffset? expiresAt) => new()
    {
        Name = "k",
        Scope = ApiKeyScope.User,
        KeyHash = "hash",
        Prefix = "bwk_0000",
        ExpiresAt = expiresAt,
    };

    private static async Task<ApiKey> StoreKeyAsync(WallTestHarness h)
    {
        var key = Key(expiresAt: null);
        key.UserId = h.Owner.Id;
        await using var db = h.CreateContext();
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync();
        return key;
    }

    private static CookieValidatePrincipalContext ValidationContext(WallTestHarness h, ApiKey? key, DateTimeOffset? checkedAt)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddAuthentication().AddCookie();
        services.AddSingleton(Substitute.For<IKioskContext>());
        services.AddSingleton<ApiKeyLoginValidator>(sp => new ApiKeyLoginValidator(
            h.ApiKeyService, h.DbContextFactory, Substitute.For<IKioskContext>(), ApiKeyLoginValidatorTests.Settings([h.Owner.Id])));
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };

        var principal = key is null
            ? UserCookieSignIn.BuildPrincipal(h.Owner)
            : ApiKeySessionSignIn.BuildPrincipal(h.Owner, key);
        var properties = new AuthenticationProperties { IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) };
        if (checkedAt is { } at)
        {
            ApiKeySessionSignIn.StampChecked(properties, at);
        }

        var scheme = new AuthenticationScheme(
            CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler));
        var ticket = new AuthenticationTicket(principal, properties, scheme.Name);
        return new CookieValidatePrincipalContext(http, scheme, new CookieAuthenticationOptions(), ticket);
    }
}
