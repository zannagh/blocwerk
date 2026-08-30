using System.Security.Claims;
using Blocwerk.Authentication;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Authentication.Providers;
using Blocwerk.Authentication.Services;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Entities;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// An API key sits on a Raspberry Pi bolted to a climbing wall, so it must be assumed to leak.
/// These tests pin the three locks that keep such a key from becoming a full login for its owner:
/// it may only authenticate on the machine-facing API paths, a bare [Authorize] can never be
/// satisfied by it, and it can never establish a Blazor circuit.
/// </summary>
public class ApiKeyPrivilegeBoundaryTests
{
    private const string WallKey = "Bearer bwk_0123456789abcdef0123456789abcdef";
    private const string Jwt = "Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.body.sig";

    /// <summary>The identifier the claims of <see cref="ApiKeyPrincipal"/> rebuild.</summary>
    private const string KeyOwnerIdentifier = "Some Climber__gh|4711";

    [Theory]
    [InlineData("/api/walls/8f1c2f6e-0000-0000-0000-000000000000/temperature")]
    [InlineData("/api/walls/8f1c2f6e-0000-0000-0000-000000000000/images")]
    [InlineData("/api/v1/me/sessions")]
    [InlineData("/API/V1/me/attempts")]
    public void SelectScheme_ForwardsAWallKeyToTheApiKeyScheme_OnTheApiSurface(string path)
    {
        Assert.Equal(ApiKeyAuthenticationHandler.SchemeName, SelectScheme(path, WallKey));
    }

    [Theory]
    [InlineData("/profile")]
    [InlineData("/walls")]
    [InlineData("/activity")]
    [InlineData("/settings/api-keys")]
    [InlineData("/api/offline/attempts")]
    [InlineData("/api/offline/boulders")]
    [InlineData("/media/walls/8f1c2f6e-0000-0000-0000-000000000000/gallery/uploaded/8f1c2f6e-0000-0000-0000-000000000001")]
    [InlineData("/")]
    [InlineData("/api/wallsomething")]
    public void SelectScheme_NeverForwardsAWallKeyToTheApiKeyScheme_OffTheApiSurface(string path)
    {
        var scheme = SelectScheme(path, WallKey);

        Assert.NotEqual(ApiKeyAuthenticationHandler.SchemeName, scheme);
        Assert.Equal(CookieAuthenticationDefaults.AuthenticationScheme, scheme);
    }

    [Fact]
    public void SelectScheme_LeavesCookieAndJwtCallersAlone()
    {
        Assert.Equal(CookieAuthenticationDefaults.AuthenticationScheme, SelectScheme("/profile", null));
        Assert.Equal(JwtBearerDefaults.AuthenticationScheme, SelectScheme("/profile", Jwt));
        Assert.Equal(JwtBearerDefaults.AuthenticationScheme, SelectScheme("/api/v1/me/sessions", Jwt));
    }

    [Fact]
    public async Task DefaultPolicy_RejectsAnApiKeyPrincipal_ButAcceptsACookieOne()
    {
        var authorization = AuthorizationService();
        var policy = AuthenticationServices.BuildHumanPolicy(new AuthorizationPolicyBuilder());

        var apiKey = await authorization.AuthorizeAsync(ApiKeyPrincipal(), resource: null, policy);
        var cookie = await authorization.AuthorizeAsync(CookiePrincipal(), resource: null, policy);
        var anonymous = await authorization.AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()), resource: null, policy);

        Assert.False(apiKey.Succeeded);
        Assert.True(cookie.Succeeded);
        Assert.False(anonymous.Succeeded);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GalleryImagePolicy_AllowsHumansAndShareTokens_ButNeverAnApiKey(bool withShareToken)
    {
        // Endpoint routing passes the Endpoint as the authorization resource, so the request has
        // to reach the handler through IHttpContextAccessor — the resource argument here is what
        // the middleware really supplies: not an HttpContext.
        var request = new DefaultHttpContext();
        if (withShareToken)
        {
            request.Request.QueryString = new QueryString("?token=abc123");
        }

        var authorization = AuthorizationService(request);
        var policy = AuthenticationServices.BuildGalleryImagePolicy(new AuthorizationPolicyBuilder());
        var endpoint = new Endpoint(_ => Task.CompletedTask, EndpointMetadataCollection.Empty, "gallery");

        // A leaked wall key must not read the galleries of every wall its owner belongs to, share
        // token or not.
        Assert.False((await authorization.AuthorizeAsync(ApiKeyPrincipal(), endpoint, policy)).Succeeded);

        Assert.True((await authorization.AuthorizeAsync(CookiePrincipal(), endpoint, policy)).Succeeded);

        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        Assert.Equal(
            withShareToken,
            (await authorization.AuthorizeAsync(anonymous, endpoint, policy)).Succeeded);
    }

    [Fact]
    public async Task AuthenticationStateProvider_TreatsAnApiKeyPrincipalAsAnonymous()
    {
        var provider = StateProvider(ApiKeyPrincipal());

        var state = await provider.GetAuthenticationStateAsync();

        Assert.NotEqual(true, state.User.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task AuthenticationStateProvider_StillReturnsACookiePrincipal()
    {
        var principal = CookiePrincipal();
        var provider = StateProvider(principal);

        var state = await provider.GetAuthenticationStateAsync();

        Assert.True(state.User.Identity?.IsAuthenticated);
        Assert.Same(principal, state.User);
    }

    [Fact]
    public async Task DenyApiKeyPrincipalFilter_TurnsAnApiKeyRequestInto404_AndLetsHumansThrough()
    {
        var filter = new DenyApiKeyPrincipalFilter();
        var sentinel = new object();

        var apiKey = await Invoke(filter, ApiKeyPrincipal(), sentinel);
        var human = await Invoke(filter, CookiePrincipal(), sentinel);
        var anonymous = await Invoke(filter, new ClaimsPrincipal(new ClaimsIdentity()), sentinel);

        Assert.IsAssignableFrom<IResult>(apiKey);
        Assert.NotSame(sentinel, apiKey);

        // The share-token and signed-in paths reach the handler untouched.
        Assert.Same(sentinel, human);
        Assert.Same(sentinel, anonymous);
    }

    [Fact]
    public async Task CurrentUserService_RefusesAnApiKeyOnAnEndpointThatDeclaredNoAuthorization()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        // The wall photo routes sit under /api/walls, so the scheme gate lets the key through to
        // the handler; nothing there ever compared the route's wall with the key's wall claim,
        // which is what made them a cross-wall read.
        var unguarded = HttpContextWith(ApiKeyPrincipal(), authorized: false);
        var service = new CurrentUserService(
            new BlocwerkSettings(),
            h.DbContextFactory,
            Substitute.For<Blocwerk.Core.Abstractions.IPasswordLoginService>(),
            Substitute.For<Blocwerk.Authentication.Services.ITotpService>(),
            authenticationStateProvider: null,
            accessor: Accessor(unguarded));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetCurrentUserAsync());
    }

    [Fact]
    public async Task CurrentUserService_StillResolvesAnApiKeyOnAGuardedEndpoint()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        // /api/v1/me/* and the wall controllers carry [Authorize] and do their own wall check, so
        // the key must keep resolving its owner there.
        var keyOwner = new User { Identifier = KeyOwnerIdentifier, DisplayName = "Some Climber" };
        await using (var db = h.DbContextFactory.CreateDbContext())
        {
            db.Users.Add(keyOwner);
            await db.SaveChangesAsync();
        }

        var guarded = HttpContextWith(ApiKeyPrincipal(), authorized: true);
        var service = new CurrentUserService(
            new BlocwerkSettings(),
            h.DbContextFactory,
            Substitute.For<Blocwerk.Core.Abstractions.IPasswordLoginService>(),
            Substitute.For<Blocwerk.Authentication.Services.ITotpService>(),
            authenticationStateProvider: null,
            accessor: Accessor(guarded));

        var user = await service.GetCurrentUserAsync();

        Assert.Equal(keyOwner.Id, user.Id);
    }

    private static async Task<object?> Invoke(IEndpointFilter filter, ClaimsPrincipal user, object sentinel)
    {
        var context = EndpointFilterInvocationContext.Create(new DefaultHttpContext { User = user });
        return await filter.InvokeAsync(context, _ => ValueTask.FromResult<object?>(sentinel));
    }

    private static IHttpContextAccessor Accessor(HttpContext context)
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(context);
        return accessor;
    }

    private static DefaultHttpContext HttpContextWith(ClaimsPrincipal user, bool authorized)
    {
        var metadata = authorized
            ? new EndpointMetadataCollection(new AuthorizeAttribute())
            : EndpointMetadataCollection.Empty;

        var context = new DefaultHttpContext { User = user };
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, metadata, "test"));
        return context;
    }

    private static string SelectScheme(string path, string? authorization)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        if (authorization is not null)
        {
            context.Request.Headers.Authorization = authorization;
        }

        return ApiKeySurface.SelectScheme(context);
    }

    private static IAuthorizationService AuthorizationService(HttpContext? request = null)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddAuthorization();

        services.AddSingleton<IAuthorizationHandler, WallGalleryImageHandler>();

        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(request);
        services.AddSingleton(accessor);

        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    private static ClaimsPrincipal ApiKeyPrincipal()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, "Some Climber"),
            new(ClaimTypes.NameIdentifier, "gh|4711"),
            new(ApiKeyClaimTypes.Scope, "Wall"),
            new(ApiKeyClaimTypes.ApiKeyId, Guid.NewGuid().ToString()),
        };

        return new ClaimsPrincipal(new ClaimsIdentity(
            claims,
            ApiKeyAuthenticationHandler.SchemeName,
            ClaimTypes.Name,
            ClaimTypes.Role));
    }

    private static ClaimsPrincipal CookiePrincipal()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, "Some Climber"),
            new(ClaimTypes.NameIdentifier, "gh|4711"),
        };

        return new ClaimsPrincipal(new ClaimsIdentity(
            claims,
            CookieAuthenticationDefaults.AuthenticationScheme,
            ClaimTypes.Name,
            ClaimTypes.Role));
    }

    private static CookieAuthenticationStateProvider StateProvider(ClaimsPrincipal user)
    {
        var context = new DefaultHttpContext { User = user };
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(context);

        // No cookie is present, so the fallback ticket path cannot rescue an API-key request.
        return new CookieAuthenticationStateProvider(accessor, Substitute.For<IDataProtectionProvider>());
    }
}
