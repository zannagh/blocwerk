using Blocwerk.Authentication.Services;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The scope, kiosk, allow-list and last-used rules of <see cref="ApiKeyLoginValidator"/> in
/// isolation — the ones the endpoint tests cannot reach without seeding walls or getting past the
/// kiosk middleware first.
/// </summary>
public sealed class ApiKeyLoginValidatorTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;
    private readonly IApiKeyService apiKeyService = Substitute.For<IApiKeyService>();
    private readonly IKioskContext kioskContext = Substitute.For<IKioskContext>();
    private readonly User user = new() { Identifier = "Climber__gh|1", DisplayName = "Climber" };
    private readonly string token;

    public ApiKeyLoginValidatorTests()
    {
        var connectionString = TestDbContextFactory.IsolatedDatabase();
        connection = new SqliteConnection(connectionString);
        connection.Open();
        factory = new TestDbContextFactory(connectionString);
        using var db = factory.CreateDbContext();
        db.Database.EnsureCreated();
        db.Users.Add(user);
        db.SaveChanges();

        (token, _) = ApiKeyTokens.Create();
    }

    [Fact]
    public async Task PersonalKeyOfAListedUser_Succeeds()
    {
        StubKey(ApiKeyScope.User, wallId: null);

        var result = await Validate(CreateValidator());

        Assert.True(result.Succeeded);
        Assert.Equal(user.Id, result.User!.Id);
    }

    [Theory]
    [InlineData(ApiKeyScope.Wall)]
    [InlineData(ApiKeyScope.Kiosk)]
    [InlineData(ApiKeyScope.Installation)]
    public async Task NonPersonalKey_IsRefused(ApiKeyScope scope)
    {
        StubKey(scope, scope is ApiKeyScope.Installation ? null : Guid.NewGuid());

        Assert.False((await Validate(CreateValidator())).Succeeded);
    }

    [Fact]
    public async Task KioskContext_IsRefusedBeforeTheKeyIsEvenLookedUp()
    {
        StubKey(ApiKeyScope.User, wallId: null);
        kioskContext.IsKiosk.Returns(true);

        var result = await Validate(CreateValidator());

        Assert.False(result.Succeeded);
        await apiKeyService.DidNotReceiveWithAnyArgs().FindActiveAsync(default!, default);
    }

    [Fact]
    public async Task LockedOutOwner_IsRefused()
    {
        StubKey(ApiKeyScope.User, wallId: null);
        await using (var db = factory.CreateDbContext())
        {
            db.Users.Single(u => u.Id == user.Id).LockoutUntil = DateTimeOffset.UtcNow.AddMinutes(5);
            await db.SaveChangesAsync();
        }

        Assert.False((await Validate(CreateValidator())).Succeeded);
    }

    [Fact]
    public async Task EmptyAllowList_RefusesEveryone_EvenWhenEnabled()
    {
        StubKey(ApiKeyScope.User, wallId: null);

        Assert.False((await Validate(CreateValidator(allowed: []))).Succeeded);
    }

    [Fact]
    public async Task AllowList_WithSomebodyElse_RefusesTheKeyOwner()
    {
        StubKey(ApiKeyScope.User, wallId: null);

        Assert.False((await Validate(CreateValidator(allowed: [Guid.NewGuid()]))).Succeeded);
    }

    [Fact]
    public async Task ListedUserWithTotp_MaySignInWithAKey()
    {
        StubKey(ApiKeyScope.User, wallId: null);
        await using (var db = factory.CreateDbContext())
        {
            var row = db.Users.Single(u => u.Id == user.Id);
            row.TotpEnabled = true;
            row.TotpSecretProtected = "protected";
            await db.SaveChangesAsync();
        }

        Assert.True((await Validate(CreateValidator())).Succeeded);
    }

    [Fact]
    public async Task LastUsedAt_IsStampedOnlyAfterTheScopeAndOwnerChecksPass()
    {
        var real = new ApiKeyService(factory, NullLogger<ApiKeyService>.Instance);
        var (installationKey, installationToken) = await SeedKeyAsync(ApiKeyScope.Installation);
        var (personalKey, personalToken) = await SeedKeyAsync(ApiKeyScope.User);
        var validator = new ApiKeyLoginValidator(real, factory, kioskContext, Settings([user.Id]));

        Assert.False((await validator.ValidateAsync(RequestWith($"Bearer {installationToken}"), default)).Succeeded);
        Assert.True((await validator.ValidateAsync(RequestWith($"Bearer {personalToken}"), default)).Succeeded);

        await using var db = factory.CreateDbContext();
        Assert.Null((await db.ApiKeys.AsNoTracking().SingleAsync(k => k.Id == installationKey)).LastUsedAt);
        Assert.NotNull((await db.ApiKeys.AsNoTracking().SingleAsync(k => k.Id == personalKey)).LastUsedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Basic Zm9vOmJhcg==")]
    [InlineData("Bearer eyJhbGciOiJIUzI1NiJ9.payload.signature")]
    public void ReadBearerApiKey_IgnoresAnythingButABwkBearer(string? header)
    {
        Assert.Null(ApiKeyLoginValidator.ReadBearerApiKey(RequestWith(header)));
    }

    public void Dispose()
    {
        connection.Dispose();
    }

    internal static BlocwerkSettings Settings(IEnumerable<Guid> allowed)
    {
        var settings = new BlocwerkSettings();
        settings.Auth.ApiKeyLogin.Enabled = true;
        settings.Auth.ApiKeyLogin.AllowedUserIds = allowed.ToHashSet();
        return settings;
    }

    private ApiKeyLoginValidator CreateValidator(Guid[]? allowed = null) =>
        new(apiKeyService, factory, kioskContext, Settings(allowed ?? [user.Id]));

    private Task<ApiKeyLoginResult> Validate(ApiKeyLoginValidator validator) =>
        validator.ValidateAsync(RequestWith($"Bearer {token}"), CancellationToken.None);

    private async Task<(Guid Id, string Token)> SeedKeyAsync(ApiKeyScope scope)
    {
        var (seedToken, prefix) = ApiKeyTokens.Create();
        var key = new ApiKey
        {
            Name = scope.ToString(),
            Scope = scope,
            UserId = user.Id,
            KeyHash = ApiKeyTokens.Hash(seedToken),
            Prefix = prefix,
        };
        await using var db = factory.CreateDbContext();
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync();
        return (key.Id, seedToken);
    }

    private void StubKey(ApiKeyScope scope, Guid? wallId)
    {
        var key = new ApiKey
        {
            Name = "Key",
            Scope = scope,
            UserId = user.Id,
            WallId = wallId,
            KeyHash = ApiKeyTokens.Hash(token),
            Prefix = token[..12],
        };
        apiKeyService.FindActiveAsync(token, Arg.Any<CancellationToken>()).Returns(key);
    }

    private static HttpRequest RequestWith(string? authorization)
    {
        var http = new DefaultHttpContext();
        if (authorization is not null)
        {
            http.Request.Headers.Authorization = authorization;
        }

        return http.Request;
    }
}
