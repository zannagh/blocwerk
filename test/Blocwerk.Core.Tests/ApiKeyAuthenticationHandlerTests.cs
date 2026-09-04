using System.Security.Claims;
using System.Text.Encodings.Web;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Guards the bearer-header routing of the API key scheme and — most importantly — that the
/// principal it builds resolves back to the SAME user identifier, so CurrentUserService finds the
/// existing user instead of silently creating a duplicate.
/// </summary>
public sealed class ApiKeyAuthenticationHandlerTests : IDisposable
{
    private const string ValidToken = "bwk_" + "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory dbContextFactory;
    private readonly IApiKeyService apiKeyService = Substitute.For<IApiKeyService>();
    private readonly User user;

    public ApiKeyAuthenticationHandlerTests()
    {
        var connectionString = TestDbContextFactory.IsolatedDatabase();
        connection = new SqliteConnection(connectionString);
        connection.Open();
        dbContextFactory = new TestDbContextFactory(connectionString);

        user = new User { Identifier = "Some Climber__gh|4711", DisplayName = "Some Climber" };
        using var db = dbContextFactory.CreateDbContext();
        db.Database.EnsureCreated();
        db.Users.Add(user);
        db.SaveChanges();
    }

    [Fact]
    public async Task NoAuthorizationHeader_YieldsNoResult()
    {
        var result = await AuthenticateAsync(authorizationHeader: null);

        Assert.True(result.None);
    }

    [Fact]
    public async Task NonApiKeyBearer_YieldsNoResult()
    {
        var result = await AuthenticateAsync("Bearer eyJhbGciOiJIUzI1NiJ9.payload.signature");

        Assert.True(result.None);
        await apiKeyService.DidNotReceiveWithAnyArgs().ValidateAsync(default!, default);
    }

    [Fact]
    public async Task UnknownOrRevokedToken_Fails()
    {
        apiKeyService.ValidateAsync(ValidToken, Arg.Any<CancellationToken>()).Returns((ApiKey?)null);

        var result = await AuthenticateAsync($"Bearer {ValidToken}");

        Assert.False(result.Succeeded);
        Assert.False(result.None);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task ValidWallKey_ProducesScopedPrincipalWithMatchingUserIdentifier()
    {
        var wallId = Guid.NewGuid();
        var key = new ApiKey
        {
            Name = "Sensor",
            Scope = ApiKeyScope.Wall,
            UserId = user.Id,
            WallId = wallId,
            KeyHash = "hash",
            Prefix = "bwk_0123",
        };
        apiKeyService.ValidateAsync(ValidToken, Arg.Any<CancellationToken>()).Returns(key);

        var result = await AuthenticateAsync($"Bearer {ValidToken}");

        Assert.True(result.Succeeded);
        var principal = result.Principal!;
        var identity = Assert.IsType<ClaimsIdentity>(principal.Identity);

        Assert.Equal(ApiKeyAuthenticationHandler.SchemeName, identity.AuthenticationType);
        Assert.Equal(user.Identifier, identity.ToUserIdentifier());
        Assert.True(principal.IsApiKeyPrincipal());
        Assert.Equal(ApiKeyScope.Wall, principal.GetApiKeyScope());
        Assert.Equal(key.Id, principal.GetApiKeyId());
        Assert.Equal(wallId, principal.GetApiKeyWallId());
    }

    [Fact]
    public async Task ValidUserKey_CarriesUserScopeAndNoWallClaim()
    {
        var key = new ApiKey
        {
            Name = "Script",
            Scope = ApiKeyScope.User,
            UserId = user.Id,
            KeyHash = "hash",
            Prefix = "bwk_0123",
        };
        apiKeyService.ValidateAsync(ValidToken, Arg.Any<CancellationToken>()).Returns(key);

        var result = await AuthenticateAsync($"Bearer {ValidToken}");

        Assert.True(result.Succeeded);
        var principal = result.Principal!;
        Assert.Equal(ApiKeyScope.User, principal.GetApiKeyScope());
        Assert.Null(principal.GetApiKeyWallId());
        Assert.Equal("User", principal.FindFirstValue(ApiKeyClaimTypes.Scope));
        Assert.Equal(user.Identifier, ((ClaimsIdentity)principal.Identity!).ToUserIdentifier());
    }

    [Fact]
    public async Task ValidInstallationKey_CarriesInstallationScopeAndNoWallClaim()
    {
        var key = new ApiKey
        {
            Name = "Deploy hook",
            Scope = ApiKeyScope.Installation,
            UserId = user.Id,
            KeyHash = "hash",
            Prefix = "bwk_0123",
        };
        apiKeyService.ValidateAsync(ValidToken, Arg.Any<CancellationToken>()).Returns(key);

        var result = await AuthenticateAsync($"Bearer {ValidToken}");

        Assert.True(result.Succeeded);
        var principal = result.Principal!;

        // The scope claim is what the InstallationApiKey policy matches on, and there is no wall to
        // claim: this key belongs to the installation, not to a place in it.
        Assert.Equal(ApiKeyScope.Installation, principal.GetApiKeyScope());
        Assert.Equal("Installation", principal.FindFirstValue(ApiKeyClaimTypes.Scope));
        Assert.Null(principal.GetApiKeyWallId());
    }

    [Fact]
    public async Task ChallengeWritesPlain401()
    {
        var context = new DefaultHttpContext();
        var handler = await CreateHandlerAsync(context);

        await handler.ChallengeAsync(properties: null);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Location"));
    }

    public void Dispose() => connection.Dispose();

    private async Task<AuthenticateResult> AuthenticateAsync(string? authorizationHeader)
    {
        var context = new DefaultHttpContext();
        if (authorizationHeader is not null)
        {
            context.Request.Headers.Authorization = authorizationHeader;
        }

        var handler = await CreateHandlerAsync(context);
        return await handler.AuthenticateAsync();
    }

    private async Task<ApiKeyAuthenticationHandler> CreateHandlerAsync(HttpContext context)
    {
        var options = Substitute.For<IOptionsMonitor<ApiKeyAuthenticationOptions>>();
        options.Get(Arg.Any<string>()).Returns(new ApiKeyAuthenticationOptions());

        var handler = new ApiKeyAuthenticationHandler(
            options,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            apiKeyService,
            dbContextFactory);

        var scheme = new AuthenticationScheme(
            ApiKeyAuthenticationHandler.SchemeName,
            ApiKeyAuthenticationHandler.SchemeName,
            typeof(ApiKeyAuthenticationHandler));

        await handler.InitializeAsync(scheme, context);
        return handler;
    }
}
