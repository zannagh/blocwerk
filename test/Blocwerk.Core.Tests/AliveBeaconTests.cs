using System.Text.Json;
using Blocwerk.Core.Services;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>GET /alive</c> as it appears ON THE WIRE. Every other test in this suite talks to
/// <see cref="AliveResponse"/> as a C# record, which is precisely the thing the browser never sees.
/// </summary>
/// <remarks>
/// This exists because the feature has a silent-death mode that a green suite would happily hide.
/// <c>maintenance.js</c> reads <c>body.instanceId</c> and treats a body without a non-empty string
/// there as "no information" — never as an error. So a global <c>PropertyNamingPolicy</c> change
/// (to snake_case, to PascalCase, to anything) would make <c>readAlive()</c> return null forever:
/// no client would ever capture a baseline, no client would ever detect a new instance, and every
/// tablet in the gym would sit on a dead circuit through every future deploy — with the controller
/// tests, the announcer tests and the route tests all still passing. The endpoint is therefore
/// EXECUTED here and the raw JSON is asserted, so the wire names are pinned by the same serializer
/// configuration the app actually runs.
/// </remarks>
public class AliveBeaconTests
{
    /// <summary>
    /// The exact keys <c>maintenance.js</c> reads. Changing any of them is a breaking change to a
    /// client that is already deployed in browsers, so it has to be a deliberate one.
    /// </summary>
    private static readonly string[] ExpectedWireNames =
    [
        "instanceId",
        "startedAt",
        "maintenance",
        "message",
        "maintenanceExpiresAt",
    ];

    [Fact]
    public async Task Alive_SerialisesTheExactPropertyNamesTheClientReads()
    {
        using var host = new AliveHost();

        using var body = await host.GetAsync();

        Assert.Equal(
            ExpectedWireNames.OrderBy(n => n, StringComparer.Ordinal),
            body.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));

        // The one the whole feature turns on: a non-empty string, which is what readAlive() demands
        // before it will treat the response as information at all.
        var instanceId = body.RootElement.GetProperty("instanceId");
        Assert.Equal(JsonValueKind.String, instanceId.ValueKind);
        Assert.False(string.IsNullOrEmpty(instanceId.GetString()));
    }

    [Fact]
    public async Task Alive_ReportsNoMaintenance_WhenNothingWasAnnounced()
    {
        using var host = new AliveHost();

        using var body = await host.GetAsync();

        Assert.False(body.RootElement.GetProperty("maintenance").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("message").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("maintenanceExpiresAt").ValueKind);
    }

    [Fact]
    public async Task Alive_CarriesTheLiveAnnouncement_AndDropsItWhenItExpires()
    {
        var clock = new MutableTestClock(new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero));
        using var host = new AliveHost(new MaintenanceAnnouncer(clock));

        host.Announcer.Announce("Back in a minute", TimeSpan.FromMinutes(2));

        using (var live = await host.GetAsync())
        {
            Assert.True(live.RootElement.GetProperty("maintenance").GetBoolean());
            Assert.Equal("Back in a minute", live.RootElement.GetProperty("message").GetString());
            Assert.Equal(JsonValueKind.String, live.RootElement.GetProperty("maintenanceExpiresAt").ValueKind);
        }

        // Expiry is decided on read, so the beacon stops claiming an update with nothing having
        // had to fire. This is what makes a failed deploy self-healing rather than a stuck banner.
        clock.Advance(TimeSpan.FromMinutes(2));

        using var expired = await host.GetAsync();
        Assert.False(expired.RootElement.GetProperty("maintenance").GetBoolean());
        Assert.Equal(JsonValueKind.Null, expired.RootElement.GetProperty("maintenanceExpiresAt").ValueKind);
    }

    /// <summary>
    /// <c>cache: 'no-store'</c> in the fetch options binds the REQUEST only. A CDN or caching
    /// reverse proxy in front of the app would be free to serve a stored 200 — pinning the instance
    /// id to the process that has just been replaced, so every client polls forever and none of them
    /// ever reloads. The response has to say so itself.
    /// </summary>
    [Fact]
    public async Task Alive_ForbidsCaching_SoAProxyCannotPinTheInstanceId()
    {
        using var host = new AliveHost();

        var context = await host.InvokeAsync();

        var cacheControl = Assert.Single(context.Response.Headers.CacheControl!);
        Assert.Contains("no-store", cacheControl!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-cache", cacheControl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-cache", Assert.Single(context.Response.Headers.Pragma!)!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A real host cannot be started here (startup migrates Postgres), so the beacon is mapped on a
    /// bare <see cref="WebApplication"/> and its endpoint delegate is executed directly. That still
    /// runs the genuine route, the genuine handler and the genuine JSON serializer configuration,
    /// which is the whole point — a copy of the response record would pin nothing.
    /// </summary>
    private sealed class AliveHost : IDisposable
    {
        private readonly WebApplication app;

        public AliveHost(IMaintenanceAnnouncer? announcer = null)
        {
            Announcer = announcer ?? new MaintenanceAnnouncer(new MutableTestClock(DateTimeOffset.UnixEpoch));

            var builder = WebApplication.CreateBuilder();
            builder.Services.AddSingleton(Announcer);
            app = builder.Build();
            app.MapAlive();
        }

        public IMaintenanceAnnouncer Announcer { get; }

        public async Task<HttpContext> InvokeAsync()
        {
            var endpoint = ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .Single(e => e.RoutePattern.RawText == "/alive");

            var context = new DefaultHttpContext
            {
                RequestServices = app.Services,
            };
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = "/alive";
            context.Response.Body = new MemoryStream();

            await endpoint.RequestDelegate!(context);
            context.Response.Body.Position = 0;
            return context;
        }

        public async Task<JsonDocument> GetAsync()
        {
            var context = await InvokeAsync();
            return await JsonDocument.ParseAsync(context.Response.Body);
        }

        public void Dispose()
        {
            ((IDisposable)app).Dispose();
        }
    }
}
