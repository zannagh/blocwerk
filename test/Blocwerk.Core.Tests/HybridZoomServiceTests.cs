// <copyright file="HybridZoomServiceTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Security.Claims;
using System.Text.Json;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.HybridZoom;
using Blocwerk.Core.Services.HybridZoom;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SkiaSharp;
using static Blocwerk.Core.Tests.HybridZoomScene;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The hybrid zoom end to end over a real database and file store: a panel photo with placed holds, an active model
/// with a solved close camera, and that camera's retained capture photo.
/// </summary>
public class HybridZoomServiceTests
{
    [Fact]
    public async Task ZoomedView_GetsTheCloseUp_AndAnUnchangedOneIsNotRenderedAgain()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var panelId = await SeedAsync(h, s, captureAge: TimeSpan.FromHours(1));
        var service = Service(h, s);

        var first = await service.RenderAsync(h.WallId, panelId, View, 512, 1.0, null, CancellationToken.None);

        Assert.Equal(CloseUpStatus.Rendered, first.Status);
        Assert.Equal("p01", first.PhotoName);
        using (var bitmap = SKBitmap.Decode(first.Image!))
        {
            Assert.Equal(bitmap.Width, bitmap.Height);
        }

        var again = await service.RenderAsync(h.WallId, panelId, View, 512, 1.0, first.ETag, CancellationToken.None);
        Assert.Equal(CloseUpStatus.Rendered, again.Status);
        Assert.Null(again.Image);
        Assert.Equal(first.ETag, again.ETag);
    }

    [Fact]
    public async Task PanelPhotographedLongAfterTheCapture_GetsNoCloseUp()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var panelId = await SeedAsync(h, s, captureAge: TimeSpan.FromDays(3));

        var result = await Service(h, s).RenderAsync(h.WallId, panelId, View, 512, 1.0, null, CancellationToken.None);

        Assert.Equal(CloseUpStatus.StaleCapture, result.Status);
    }

    [Fact]
    public async Task Endpoint_IsNotFoundWhileOff_AndForOutsiders_AndSaysWhyWhenThereIsNothingBetter()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var panelId = await SeedAsync(h, s, captureAge: TimeSpan.FromHours(1));

        Assert.IsType<NotFound>((await ServeAsync(h, s, panelId, enabled: false, wallId: h.WallId)).Result);
        Assert.IsType<NotFound>((await ServeAsync(h, s, panelId, enabled: true, wallId: Guid.NewGuid())).Result);

        var (ok, okHttp) = await ServeAsync(h, s, panelId, enabled: true, wallId: h.WallId);
        Assert.Equal("image/jpeg", Assert.IsType<FileContentHttpResult>(ok).ContentType);
        Assert.False(string.IsNullOrEmpty(okHttp.Response.Headers[WallPanelCloseUpEndpoint.RectHeader]));

        // The whole panel: the close camera does not cover it, so the panel photo stays.
        var (none, noneHttp) = await ServeAsync(h, s, panelId, enabled: true, wallId: h.WallId, rect: (0, 0, 1, 1));
        Assert.IsType<NoContent>(none);
        Assert.Equal("no-better-photo", noneHttp.Response.Headers[WallPanelCloseUpEndpoint.StatusHeader]);
    }

    internal static HybridZoomService Service(WallTestHarness h, CaptureScenario s, HybridZoomOptions? options = null)
    {
        options ??= new HybridZoomOptions { Enabled = true };
        return new HybridZoomService(h.DbContextFactory, s.Files, options, new HybridZoomCache(options), NullLogger<HybridZoomService>.Instance);
    }

    internal static Task<(IResult Result, HttpContext Http)> ServeAsync(
        WallTestHarness h, CaptureScenario s, Guid panelId, bool enabled, Guid wallId, (double X, double Y, double W, double H)? rect = null)
    {
        var r = rect ?? (View.X, View.Y, View.W, View.H);
        return ServeAsync(h, s, panelId, wallId, new HybridZoomOptions { Enabled = enabled }, Substitute.For<IKioskContext>(), null, Query(r));
    }

    /// <summary>Calls the endpoint as the harness's current user (or anonymous), with the given kiosk state and query.</summary>
    internal static async Task<(IResult Result, HttpContext Http)> ServeAsync(
        WallTestHarness h, CaptureScenario s, Guid panelId, Guid wallId, HybridZoomOptions options, IKioskContext kiosk, string? token, string query)
    {
        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString("?" + query + (token is null ? string.Empty : "&token=" + Uri.EscapeDataString(token)));
        var result = await WallPanelCloseUpEndpoint.HandleAsync(
            wallId, panelId, new ClaimsPrincipal(), http, options, Service(h, s, options), h.CurrentUser, h.DbContextFactory, kiosk, CancellationToken.None);
        return (result, http);
    }

    /// <summary>The query of a view rectangle at 512 px, square.</summary>
    internal static string Query((double X, double Y, double W, double H) r) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"x={r.X}&y={r.Y}&w={r.W}&h={r.H}&px=512&ar=1");

    /// <summary>Seeds the scene; returns the panel. The capture ran <paramref name="captureAge"/> before the panel photo.</summary>
    internal static async Task<Guid> SeedAsync(WallTestHarness h, CaptureScenario s, TimeSpan captureAge)
    {
        await h.SeedWallAsync(holdCount: 0, generation: 1);
        var close = LookAt("p01", [1000, -1600, 1000], Target);
        var stored = await s.Files.SaveAsync(Jpeg(800, 600), ".jpg", CancellationToken.None);
        await using var db = h.CreateContext();
        var now = DateTimeOffset.UtcNow;
        var panel = new WallPanel { WallId = h.WallId, Generation = 1, Photo = Jpeg(400, 300), PhotoContentType = "image/jpeg", CreatedAt = now };
        var model = new WallGeometryModel { WallId = h.WallId, Json = ModelJson(close), Source = "test", IsActive = true, SchemaVersion = 1 };
        var capture = new WallCapture
        {
            WallId = h.WallId, CreatedByUserId = h.Owner.Id, Status = WallCaptureStatus.Succeeded, GeometryModelId = model.Id, CreatedAt = now - captureAge,
        };
        db.AddRange(panel, model, capture);
        db.WallCapturePhotos.Add(new WallCapturePhoto
        {
            CaptureId = capture.Id, Index = 1, StoredPath = stored, ContentHash = new string('a', 64), Width = 800, Height = 600,
            UploadedAt = now - captureAge,
        });
        for (var i = 0; i < 12; i++)
        {
            double a = 200 + ((i % 4) * 500), b = 300 + ((i / 4) * 600);
            var px = Panel.Project(HybridZoomScene.Wall.ToWorld(a, b))!.Value;
            db.Holds.Add(new Hold
            {
                WallId = h.WallId, WallPanelId = panel.Id, Generation = 1, X = px.X / Width, Y = px.Y / Height, Radius = 0.01,
                FacetId = "0", PlaneAMm = a, PlaneBMm = b,
            });
        }

        await db.SaveChangesAsync();
        return panel.Id;
    }

    private static string ModelJson(SolvedCamera camera) => JsonSerializer.Serialize(new
    {
        version = 1,
        segments = new[]
        {
            new { index = 0, facets = new[] { new { id = "0", origin = new[] { 0.0, 0, 0 }, u = new[] { 1.0, 0, 0 }, v = new[] { 0.0, 0, 1 }, normal = new[] { 0.0, -1, 0 } } } },
        },
        cameras = new[]
        {
            new { image = camera.Image, width = camera.Width, height = camera.Height, K = camera.K, dist = camera.Dist, R = camera.R, t = camera.T },
        },
    });

    private static byte[] Jpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(new SKColor(90, 140, 200));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }
}
