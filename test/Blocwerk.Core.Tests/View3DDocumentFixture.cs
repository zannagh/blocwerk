// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Assets.Documents;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The <c>/d/{kind}/{sha}</c> route over <see cref="AssetRouteFixture"/>'s walls, callers and kiosk: the real endpoint and
/// <see cref="View3DDocumentService"/>, a real <see cref="View3DDocumentStore"/>, and a stand-in view service for rebuilds.
/// </summary>
public sealed class View3DDocumentFixture : IDisposable
{
    private WebApplication? app;

    public View3DDocumentFixture()
    {
        Views.BuildAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(c => Task.FromResult(new Wall3DViewResult(Wall3DViewStatus.Ok, "W", ViewOf(c.ArgAt<Guid>(0)))));
    }

    public AssetRouteFixture Assets { get; } = new();

    public View3DDocumentStore Store { get; private set; } = new();

    public IWall3DViewService Views { get; } = Substitute.For<IWall3DViewService>();

    public Guid WallId => Assets.Harness.WallId;

    /// <summary>The view the stand-in service builds for a wall: one texture, so the document has a token to strip.</summary>
    public static Wall3DView ViewOf(Guid wallId) => new()
    {
        WallId = wallId,
        WallName = "W",
        Textures = [new Wall3DTexture("F1", $"/api/walls/{wallId}/a/{new string('c', 64)}", new Geometry.PlaneRectMm(0, 1, 0, 1))],
    };

    public async Task<string> SeedAsync()
    {
        await Assets.SeedAsync();
        return Store.Publish(ViewOf(WallId)).Sha;
    }

    /// <summary>A restart: the store forgets every document (the next request rebuilds).</summary>
    public void Restart()
    {
        Store = new View3DDocumentStore();
        (app as IDisposable)?.Dispose();
        app = null;
    }

    public async Task<DefaultHttpContext> GetAsync(
        Guid wallId, string sha, string kind = View3DDocument.Kind, string? query = null, string method = "GET", Action<HttpContext>? configure = null)
    {
        app ??= BuildApp();
        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().Single(e => e.RoutePattern.RawText == DocumentEndpoints.Route);
        var http = new DefaultHttpContext { RequestServices = app.Services };
        http.Request.Method = method;
        http.Request.RouteValues["wallId"] = wallId.ToString();
        http.Request.RouteValues["kind"] = kind;
        http.Request.RouteValues["sha"] = sha;
        if (query is not null)
        {
            http.Request.QueryString = new QueryString(query);
        }

        configure?.Invoke(http);
        http.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(http);
        return http;
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
        builder.Services.AddSingleton(_ => new View3DDocumentService(
            Assets.Harness.DbContextFactory, Assets.CurrentUser, Views, Store, TimeProvider.System,
            NullLogger<View3DDocumentService>.Instance, Assets.Kiosk, Assets.KioskKeys));
        var built = builder.Build();
        built.MapWallDocuments();
        return built;
    }
}
