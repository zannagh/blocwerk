// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Assets;
using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Assets.Manifest;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The <see cref="AssetRouteFixture"/> wall (one hashed asset of every kind, its 3D model made active) with the manifest
/// service over it: the same caller and kiosk substitutes decide both the manifest and the <c>/a/</c> route, so a test
/// can fetch every sha a manifest lists as the caller who got it.
/// </summary>
public sealed class ManifestFixture : IDisposable
{
    private WebApplication? app;

    public ManifestFixture(PostgresTestDatabase? postgres = null)
    {
        Assets = new AssetRouteFixture(postgres);
        Feed = new WallFeed(() => Assets.Harness.RootContextFactory.CreateDbContext(), NullLogger<WallFeed>.Instance);
    }

    public AssetRouteFixture Assets { get; }

    public IWallFeed Feed { get; }

    public WallManifestCache Cache { get; } = new();

    public MutableTestClock Time { get; } = new(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));

    public Guid WallId => Assets.Harness.WallId;

    public async Task<SeededAssets> SeedAsync()
    {
        var seeded = await Assets.SeedAsync();
        await using var db = Assets.Harness.CreateContext();
        await db.WallGeometryModels.ExecuteUpdateAsync(s => s.SetProperty(m => m.IsActive, m => m.WallId == WallId));
        return seeded;
    }

    public WallManifestService Service() => new(
        Assets.Harness.DbContextFactory,
        Assets.CurrentUser,
        new WallManifestBuilder(Assets.Harness.RootContextFactory, Assets.ImageStorage, Assets.CaptureFiles),
        Cache,
        Feed,
        Time,
        NullLogger<WallManifestService>.Instance,
        Assets.Kiosk,
        Assets.KioskKeys);

    public Task<WallManifest?> GetAsync(string? token = null) => Service().GetAsync(WallId, token, default);

    /// <summary>Calls the real manifest endpoint in-process.</summary>
    public async Task<DefaultHttpContext> GetHttpAsync(string? token = null, Action<HttpContext>? configure = null)
    {
        app ??= BuildApp();
        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().Single(e => e.RoutePattern.RawText == ManifestEndpoints.ManifestRoute);
        var http = new DefaultHttpContext { RequestServices = app.Services };
        http.Request.Method = HttpMethods.Get;
        http.Request.RouteValues["wallId"] = WallId.ToString();
        if (token is not null)
        {
            http.Request.QueryString = new QueryString("?token=" + Uri.EscapeDataString(token));
        }

        configure?.Invoke(http);
        http.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(http);
        return http;
    }

    /// <summary>Writes a feed row of <paramref name="kind"/> for the wall straight into the table.</summary>
    public async Task AddFeedRowAsync(string kind)
    {
        await using var db = Assets.Harness.CreateContext();
        db.WallChanges.Add(new WallChange
        {
            WallId = WallId, Kind = kind, EntityId = WallId, Audience = FeedAudience.Wall, SchemaVersion = FeedSchema.PayloadVersion,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Makes the caller an anonymous tablet bound to the wall, with a kiosk key.</summary>
    public void BindKiosk(Guid? wallId = null)
    {
        Assets.BindKiosk(wallId ?? WallId);
        Assets.Kiosk.KioskApiKeyId.Returns(Guid.Parse("6f1c0a6e-0000-4000-8000-00000000000a"));
        Assets.Caller = null;
    }

    public void Dispose()
    {
        (app as IDisposable)?.Dispose();
        Assets.Dispose();
    }

    private WebApplication BuildApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped(_ => Service());
        var built = builder.Build();
        built.MapAssetManifest();
        return built;
    }
}
