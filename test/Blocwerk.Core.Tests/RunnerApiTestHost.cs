// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Net.Http.Headers;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Endpoints;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Blocwerk.Core.Tests;

/// <summary>
/// An in-memory host with the REAL runner API (<see cref="RunnerApiEndpoints"/>), BOTH rate-limit policies registered
/// the way Program does (the API-key login's and the runners'), and the app's scheme selector
/// (<see cref="ApiKeySurface.SelectScheme"/>) in front of permissive JWT / API-key handlers, plus probes that echo who
/// the caller is. The queue is a <see cref="RunnerFixture"/>'s (SQLite, test clock).
/// </summary>
internal sealed class RunnerApiTestHost : IAsyncDisposable
{
    public const string LoginProbe = "/probe/login-limited";

    private readonly WebApplication app;

    private RunnerApiTestHost(WebApplication app, RunnerFixture fixture)
    {
        this.app = app;
        Fixture = fixture;
        Client = app.GetTestClient();
    }

    public RunnerFixture Fixture { get; }

    public HttpClient Client { get; }

    public static async Task<RunnerApiTestHost> StartAsync(RunnerFixture fixture)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(fixture.Queue);
        builder.Services.AddAuthentication("BlocwerkPolicy")
            .AddPolicyScheme("BlocwerkPolicy", "BlocwerkPolicy", o => o.ForwardDefaultSelector = ApiKeySurface.SelectScheme)
            .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddScheme<AuthenticationSchemeOptions, PermissiveAuthHandler>(JwtBearerDefaults.AuthenticationScheme, _ => { })
            .AddScheme<AuthenticationSchemeOptions, PermissiveAuthHandler>(ApiKeyAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddApiKeyLogin();

        // Program's policy, but refilled hourly instead of every 10 s: on a loaded machine the wall clock must not top
        // up a burst test halfway through.
        RunnerApiEndpoints.AddRunnerRateLimit(builder.Services, TimeSpan.FromHours(1));

        var app = builder.Build();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapRunnerApi();
        app.MapPost(LoginProbe, () => Results.NoContent()).RequireRateLimiting(ApiKeyLoginEndpoints.RateLimitPolicy);
        foreach (var path in new[] { "/api/v1/me", "/api/captures/probe", "/api/walls/{wallId:guid}/probe" })
        {
            app.MapGet(path, (HttpContext http) => http.User.Identity?.IsAuthenticated == true ? http.User.Identity.Name! : "anonymous");
        }

        await app.StartAsync();
        return new RunnerApiTestHost(app, fixture);
    }

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? key, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        if (key is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        return Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> PostJsonAsync(string path, string? key, string json) =>
        SendAsync(HttpMethod.Post, path, key, new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
