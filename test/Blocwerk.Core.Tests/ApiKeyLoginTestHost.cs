using Blocwerk.Authentication;
using Blocwerk.Authentication.Endpoints;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// An in-memory PRODUCTION-environment host carrying just what <c>POST /account/api-key-login</c>
/// needs: the real middleware order from <c>ConfigureAuthenticationMiddlewares</c> (rate limiter,
/// cookie authentication, the kiosk gate, authorization, antiforgery), the real
/// <see cref="ApiKeyService"/> over SQLite, and a <c>/whoami</c> probe that echoes the cookie
/// session's <c>uid</c> claim.
/// </summary>
internal sealed class ApiKeyLoginTestHost : IAsyncDisposable
{
    public const string WhoAmIPath = "/whoami";

    /// <summary>Echoes the session's key-session claims as "amr|api_key_id".</summary>
    public const string ClaimsPath = "/whoami/claims";

    private readonly SqliteConnection connection;
    private readonly WebApplication app;

    private ApiKeyLoginTestHost(
        SqliteConnection connection, TestDbContextFactory factory, WebApplication app, BlocwerkSettings settings)
    {
        this.connection = connection;
        this.app = app;
        Factory = factory;
        Settings = settings;
        Client = app.GetTestClient();
    }

    public TestDbContextFactory Factory { get; }

    /// <summary>The live settings singleton; the allow-list is read from it on every request.</summary>
    public BlocwerkSettings Settings { get; }

    public HttpClient Client { get; }

    public static async Task<ApiKeyLoginTestHost> StartAsync(bool enabled, bool isKiosk = false)
    {
        var connectionString = TestDbContextFactory.IsolatedDatabase();
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        var factory = new TestDbContextFactory(connectionString);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.UseTestServer();
        builder.Configuration["Blocwerk:Auth:ApiKeyLogin:Enabled"] = enabled ? "true" : "false";
        var settings = new BlocwerkSettings(builder.Configuration);

        var kioskContext = Substitute.For<IKioskContext>();
        kioskContext.IsKiosk.Returns(isKiosk);

        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<IDbContextFactory<BlocwerkDbContext>>(factory);
        builder.Services.AddScoped<IApiKeyService, ApiKeyService>();
        builder.Services.AddScoped(_ => kioskContext);
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
        builder.Services.AddAuthorization();
        builder.Services.AddAntiforgery();
        builder.Services.AddApiKeyLogin();

        var app = builder.Build();
        app.ConfigureAuthenticationMiddlewares();
        app.MapApiKeyLogin(settings);
        app.MapGet(WhoAmIPath, (HttpContext http) => http.User.FindFirst("uid")?.Value ?? "anonymous");
        app.MapGet(ClaimsPath, (HttpContext http) =>
            $"{http.User.FindFirst("amr")?.Value}|{http.User.FindFirst("api_key_id")?.Value}");
        await app.StartAsync();

        // Ghost is listed on purpose, so the Ghost test exercises the Ghost rule, not the allow-list.
        settings.Auth.ApiKeyLogin.AllowedUserIds = new HashSet<Guid> { GhostUser.Id };
        return new ApiKeyLoginTestHost(connection, factory, app, settings);
    }

    /// <summary>Adds a user, on the key-login allow-list unless <paramref name="allow"/> is false.</summary>
    public async Task<User> AddUserAsync(string identifier, DateTimeOffset? deletedAt = null, bool allow = true)
    {
        var user = new User { Identifier = identifier, DisplayName = identifier, DeletedAt = deletedAt };
        await using var db = Factory.CreateDbContext();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        if (allow)
        {
            Settings.Auth.ApiKeyLogin.AllowedUserIds =
                Settings.Auth.ApiKeyLogin.AllowedUserIds.Append(user.Id).ToHashSet();
        }

        return user;
    }

    /// <summary>Stores a key row the way ApiKeyService mints one, and returns its full token.</summary>
    public async Task<string> AddKeyAsync(
        Guid userId,
        ApiKeyScope scope = ApiKeyScope.User,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? revokedAt = null)
    {
        var (token, prefix) = ApiKeyTokens.Create();
        await using var db = Factory.CreateDbContext();
        db.ApiKeys.Add(new ApiKey
        {
            Name = "Playwright",
            Scope = scope,
            UserId = userId,
            KeyHash = ApiKeyTokens.Hash(token),
            Prefix = prefix,
            ExpiresAt = expiresAt,
            RevokedAt = revokedAt,
        });
        await db.SaveChangesAsync();
        return token;
    }

    public Task<HttpResponseMessage> LoginAsync(string? token, string query = "")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ApiKeyLoginEndpoints.Path + query);
        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        }

        return Client.SendAsync(request);
    }

    /// <summary>Replays the response's cookies against <see cref="WhoAmIPath"/>, as a browser would.</summary>
    public async Task<string> WhoAmIAsync(HttpResponseMessage loginResponse, string path = WhoAmIPath)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (loginResponse.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            var pairs = setCookies.Select(c => c.Split(';', 2)[0]);
            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", pairs));
        }

        var response = await Client.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        await connection.DisposeAsync();
    }
}
