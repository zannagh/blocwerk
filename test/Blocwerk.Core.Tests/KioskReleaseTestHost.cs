// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Claims;
using Blocwerk.Authentication;
using Blocwerk.Authentication.Kiosk;
using Blocwerk.Authentication.Services;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Services;
using Blocwerk.Web;
using Blocwerk.Web.Controllers;
using Blocwerk.Web.State;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// An in-memory production host for the kiosk form posts with the REAL antiforgery filter and middleware order
/// (<c>ConfigureAuthenticationMiddlewares</c>) and the kiosk error recovery, plus three probes: <c>/in</c> signs a
/// member in, <c>/device</c> registers the browser as a tablet, <c>/form</c> issues a form token for whoever the
/// request is. A tiny cookie jar stands in for the browser.
/// </summary>
internal sealed class KioskReleaseTestHost : IAsyncDisposable
{
    public static readonly Guid WallId = Guid.NewGuid();

    private readonly WebApplication app;
    private readonly Dictionary<string, string> jar = new(StringComparer.Ordinal);

    private KioskReleaseTestHost(WebApplication app)
    {
        this.app = app;
        Client = app.GetTestClient();
    }

    public HttpClient Client { get; }

    public static async Task<KioskReleaseTestHost> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.UseTestServer();
        AddServices(builder.Services);

        var app = builder.Build();
        app.UseKioskErrorRecovery();
        app.ConfigureAuthenticationMiddlewares();
        app.MapControllers();
        MapProbes(app);
        await app.StartAsync();
        return new KioskReleaseTestHost(app);
    }

    /// <summary>Drops a cookie from the jar, as the browser does when the server expires it.</summary>
    public void Forget(string name) => jar.Remove(name);

    public bool Has(string name) => jar.ContainsKey(name);

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
                Store(cookie);
            }
        }

        return response;
    }

    public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(new HttpRequestMessage(HttpMethod.Get, path));

    /// <summary>A form post, with the fetch metadata a browser's own form navigation carries.</summary>
    public Task<HttpResponseMessage> PostFormAsync(string path, IDictionary<string, string> form, string site = "same-origin")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Add("Sec-Fetch-Site", site);
        request.Headers.Add("Sec-Fetch-Dest", "document");
        request.Headers.Add("Sec-Fetch-Mode", "navigate");
        return SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.DisposeAsync();
    }

    private void Store(string setCookie)
    {
        var pair = setCookie.Split(';', 2)[0];
        var at = pair.IndexOf('=');
        var name = pair[..at];
        var value = pair[(at + 1)..];
        var expired = setCookie.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase);
        if (expired || value.Length == 0)
        {
            jar.Remove(name);
        }
        else
        {
            jar[name] = value;
        }
    }

    private static void AddServices(IServiceCollection services)
    {
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
        services.AddAuthorization();
        services.AddAntiforgery();
        services.AddRateLimiter(_ => { });
        services.AddControllersWithViews().AddApplicationPart(typeof(KioskController).Assembly);

        var kioskContext = Substitute.For<IKioskContext>();
        services.AddScoped(_ => kioskContext);
        services.AddScoped(_ => Substitute.For<IApiKeyService>());
        services.AddScoped(_ => Substitute.For<IKioskService>());
        services.AddScoped(_ => Substitute.For<ICurrentUserService>());
        services.AddScoped(_ => new KioskPendingAttemptLogger(
            Substitute.For<IBoulderService>(),
            Substitute.For<IAttemptService>(),
            Substitute.For<AuthenticationStateProvider>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<KioskPendingAttemptLogger>.Instance));
        services.AddSingleton<KioskDeviceCookie>();
        services.AddSingleton(new KioskKeyValidator(Substitute.For<IDbContextFactory<BlocwerkDbContext>>()));
        services.AddSingleton<KioskThrottleRegistry>();
        services.AddSingleton<KioskPairingRegistry>();
    }

    private static void MapProbes(WebApplication app)
    {
        app.MapGet("/in", (HttpContext http) => http.SignInAsync(
            new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "member"), new Claim("uid", Guid.NewGuid().ToString())],
                CookieAuthenticationDefaults.AuthenticationScheme))));
        app.MapGet("/device", (HttpContext http, KioskDeviceCookie cookie) =>
        {
            cookie.Write(http, Guid.NewGuid(), WallId);
            return Results.Ok();
        });
        app.MapGet("/form", (HttpContext http, IAntiforgery antiforgery) =>
            antiforgery.GetAndStoreTokens(http).RequestToken);
        app.MapGet($"/walls/{WallId}", () => Results.StatusCode(StatusCodes.Status500InternalServerError));
    }
}
