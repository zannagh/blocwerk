// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Claims;
using Blocwerk.Authentication;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.DeviceLinks;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.Controllers;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Blocwerk.Core.Tests.DeviceLinks;

/// <summary>
/// An in-memory production host for the device link's routes: the real controllers, middleware order
/// (<c>ConfigureAuthenticationMiddlewares</c>), antiforgery, rate limit and API-key handler, the real key and link
/// services over SQLite, a <c>/in</c> probe that signs the owner in and <c>/form</c> for a form token. A small cookie
/// jar stands in for the browser.
/// </summary>
internal sealed class DeviceLinkTestHost : IAsyncDisposable
{
    private readonly SqliteConnection connection;
    private readonly WebApplication app;
    private readonly Dictionary<string, string> jar = new(StringComparer.Ordinal);

    private DeviceLinkTestHost(SqliteConnection connection, WebApplication app, User owner)
    {
        this.connection = connection;
        this.app = app;
        Owner = owner;
        Client = app.GetTestClient();
    }

    public User Owner { get; }

    public HttpClient Client { get; }

    public static async Task<DeviceLinkTestHost> StartAsync()
    {
        var connectionString = TestDbContextFactory.IsolatedDatabase();
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        var factory = new TestDbContextFactory(connectionString);
        var owner = new User { Identifier = "Climber__gh|1", DisplayName = "Climber", Email = "climber@example.test" };
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            db.Users.Add(owner);
            await db.SaveChangesAsync();
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.UseTestServer();
        AddServices(builder.Services, factory, owner);

        var app = builder.Build();
        app.ConfigureAuthenticationMiddlewares();
        app.MapControllers();
        app.MapGet("/in", (HttpContext http) => http.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, owner.Identifier)], CookieAuthenticationDefaults.AuthenticationScheme))));
        app.MapGet("/form", (HttpContext http, IAntiforgery antiforgery) => antiforgery.GetAndStoreTokens(http).RequestToken);
        await app.StartAsync();
        return new DeviceLinkTestHost(connection, app, owner);
    }

    /// <summary>Sends a request with the jar's cookies and stores what the response sets.</summary>
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        if (jar.Count > 0)
        {
            request.Headers.Add("Cookie", string.Join("; ", jar.Select(c => $"{c.Key}={c.Value}")));
        }

        var response = await Client.SendAsync(request);
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            foreach (var cookie in cookies)
            {
                var pair = cookie.Split(';', 2)[0];
                var at = pair.IndexOf('=');
                jar[pair[..at]] = pair[(at + 1)..];
            }
        }

        return response;
    }

    public async Task SignInAsync() => await SendAsync(new HttpRequestMessage(HttpMethod.Get, "/in"));

    /// <summary>Posts the consent form as the page would, with a fresh antiforgery token unless told not to.</summary>
    public async Task<HttpResponseMessage> PostConsentAsync(IDictionary<string, string> fields, bool withToken = true)
    {
        var form = new Dictionary<string, string>(fields);
        if (withToken)
        {
            var tokenResponse = await SendAsync(new HttpRequestMessage(HttpMethod.Get, "/form"));
            form["__RequestVerificationToken"] = await tokenResponse.Content.ReadAsStringAsync();
        }

        return await SendAsync(new HttpRequestMessage(HttpMethod.Post, "/device-link/confirm") { Content = new FormUrlEncodedContent(form) });
    }

    /// <summary>A request carrying <paramref name="token"/> as its bearer key and no cookies.</summary>
    public Task<HttpResponseMessage> WithKeyAsync(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        return Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        await connection.DisposeAsync();
    }

    private static void AddServices(IServiceCollection services, TestDbContextFactory factory, User owner)
    {
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie()
            .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, _ => { });
        services.AddAuthorization(options => options.AddPolicy(
            BlocwerkPolicies.UserApiKey,
            policy => AuthenticationServices.BuildApiKeyPolicy(policy, ApiKeyScope.User)));
        services.AddAntiforgery();
        services.AddDeviceLinkRateLimit();
        services.AddControllersWithViews().AddApplicationPart(typeof(DeviceLinkController).Assembly);

        var kioskContext = Substitute.For<IKioskContext>();
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.GetCurrentUserAsync().Returns(owner);
        services.AddScoped(_ => kioskContext);
        services.AddScoped(_ => currentUser);
        services.AddSingleton<IDbContextFactory<BlocwerkDbContext>>(factory);
        services.AddScoped<IApiKeyService, ApiKeyService>();
        services.AddScoped<IDeviceLinkService, DeviceLinkService>();
    }
}
