// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Net;
using System.Net.Http.Json;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.AppleSignIn;
using Blocwerk.Core.Data;
using Blocwerk.Core.Services;
using Blocwerk.Web.Controllers;
using Blocwerk.Web.DeviceLinks;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Blocwerk.Core.Tests.AppleSignIn;

/// <summary>
/// An in-memory host for the anonymous Sign in with Apple routes: the real controller, rate limiter and nonce cap,
/// the real service over the test bed's database with <see cref="StubAppleValidator"/>.
/// </summary>
internal sealed class AppleSignInTestHost : IAsyncDisposable
{
    /// <summary>The header a test sets to send a request from another client address.</summary>
    public const string AddressHeader = "X-Test-Remote-Address";

    private readonly WebApplication app;

    private AppleSignInTestHost(WebApplication app, AppleSignInTestBed bed)
    {
        this.app = app;
        Bed = bed;
        Client = app.GetTestClient();
    }

    public AppleSignInTestBed Bed { get; }

    public HttpClient Client { get; }

    public static async Task<AppleSignInTestHost> StartAsync(int noncesPerMinute = AppleNonceIssuanceCap.DefaultNoncesPerMinute)
    {
        var bed = AppleSignInTestBed.Create();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddAppleSignInWeb();
        builder.Services.AddSingleton(new AppleNonceIssuanceCap(noncesPerMinute));
        builder.Services.AddSingleton<IAppleNonceCodec>(bed.Codec);
        builder.Services.AddControllers().AddApplicationPart(typeof(AppleSignInController).Assembly);
        builder.Services.AddSingleton<IDbContextFactory<BlocwerkDbContext>>(bed.Contexts);
        builder.Services.AddScoped(_ => Substitute.For<IKioskContext>());
        builder.Services.AddScoped<IApiKeyService, ApiKeyService>();
        builder.Services.AddSingleton<IAppleIdentityTokenValidator, StubAppleValidator>();
        builder.Services.AddScoped<AppleNonceStore>();
        builder.Services.AddScoped<IAppleSignInService, AppleSignInService>();

        var app = builder.Build();
        app.Use((http, next) =>
        {
            if (http.Request.Headers.TryGetValue(AddressHeader, out var address))
            {
                http.Connection.RemoteIpAddress = IPAddress.Parse(address.ToString());
            }

            return next(http);
        });
        app.UseRateLimiter();
        app.MapControllers();
        await app.StartAsync();
        return new AppleSignInTestHost(app, bed);
    }

    public async Task<string> NonceAsync(string address = "10.0.0.1")
    {
        var response = await SendAsync("/api/device-link/apple/nonce", null, address);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AppleNonceResponse>())!.Nonce;
    }

    /// <summary>Signs in with a fresh nonce; a token of <c>ok:&lt;subject&gt;</c> verifies, anything else does not.</summary>
    public async Task<HttpResponseMessage> SignInAsync(string token, string address = "10.0.0.1")
    {
        var nonce = await NonceAsync(address);
        return await SendAsync("/api/device-link/apple", new { identityToken = token, nonce, device = "Test iPhone" }, address);
    }

    public Task<HttpResponseMessage> SendAsync(string path, object? body, string address)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { }) };
        request.Headers.Add(AddressHeader, address);
        return Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        Bed.Dispose();
    }
}
