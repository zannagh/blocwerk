// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blocwerk.Core.Tests;

/// <summary>
/// An in-memory host with only the REAL storage-probe endpoint, a header-driven sign-in
/// (<see cref="StorageProbeAuthHandler"/>) and a logger that keeps what the endpoint logs.
/// </summary>
internal sealed class StorageProbeTestHost : IAsyncDisposable
{
    public const string Who = "X-Who";

    private readonly WebApplication app;

    private StorageProbeTestHost(WebApplication app, StorageProbeLogSink logs)
    {
        this.app = app;
        Logs = logs;
        Client = app.GetTestClient();
    }

    public HttpClient Client { get; }

    public StorageProbeLogSink Logs { get; }

    public static async Task<StorageProbeTestHost> StartAsync()
    {
        var logs = new StorageProbeLogSink();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, StorageProbeAuthHandler>("test", null);
        builder.Services.AddAuthorization();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapStorageProbe();
        await app.StartAsync();
        return new StorageProbeTestHost(app, logs);
    }

    public Task<HttpResponseMessage> PostAsync(string? who, HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, StorageProbeEndpoints.Route) { Content = content };
        if (who is not null)
        {
            request.Headers.Add(Who, who);
        }

        return Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }
}

/// <summary>Keeps every Blocwerk.StorageProbe log entry, level and rendered message.</summary>
internal sealed class StorageProbeLogSink : ILoggerProvider
{
    public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new SinkLogger(categoryName == "Blocwerk.StorageProbe" ? this : null);

    public void Dispose()
    {
    }

    private sealed class SinkLogger(StorageProbeLogSink? sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => sink is not null;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            sink?.Entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}

/// <summary>
/// "person" signs in as a browser user, "person-without-id" as one with no NameIdentifier claim, and
/// "personal-write-key" as an API key; anything else is anonymous.
/// </summary>
internal sealed class StorageProbeAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var who = Request.Headers[StorageProbeTestHost.Who].ToString();
        var type = who switch
        {
            "person" or "person-without-id" => "test",
            "personal-write-key" => ApiKeyAuthenticationHandler.SchemeName,
            _ => null,
        };
        if (type is null)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = who == "person-without-id"
            ? [new Claim(ClaimTypes.Name, "Nobody")]
            : new[] { new Claim(ClaimTypes.NameIdentifier, "probe|" + Guid.NewGuid()) };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, type));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "test")));
    }
}
