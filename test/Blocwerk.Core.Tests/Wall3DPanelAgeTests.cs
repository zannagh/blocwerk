// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The 3D page's "photos are older than the panel photos" note dates the model by the capture it came from, following
/// corrections back to the original capture, so a correction's fresh model row does not hide it.
/// </summary>
public class Wall3DPanelAgeTests
{
    [Fact]
    public async Task ACorrectedModel_IsDatedByTheOriginalCapture_SoNewerPanelsStillShowTheNote()
    {
        using var harness = new WallTestHarness();
        await SeedAsync(harness, panelAgeAfterCapture: TimeSpan.FromDays(3));

        var view = (await NewService(harness).BuildAsync(harness.WallId, null)).View!;

        Assert.Equal(1, view.PanelsNewerThanModelCount);
    }

    [Fact]
    public async Task APanelFromTheSameVisitAsTheCapture_ShowsNoNote()
    {
        using var harness = new WallTestHarness();
        await SeedAsync(harness, panelAgeAfterCapture: TimeSpan.FromMinutes(20));

        var view = (await NewService(harness).BuildAsync(harness.WallId, null)).View!;

        Assert.Equal(0, view.PanelsNewerThanModelCount);
    }

    private static Wall3DViewService NewService(WallTestHarness harness) =>
        new(harness.WallService, harness.CurrentUser, harness.DbContextFactory, Wall3DViewAccessTests.Captures(harness), NullLogger<Wall3DViewService>.Instance);

    private static async Task SeedAsync(WallTestHarness harness, TimeSpan panelAgeAfterCapture)
    {
        await harness.SeedWallAsync(holdCount: 1);
        var captureAt = DateTimeOffset.UtcNow.AddDays(-30);
        await using var db = harness.CreateContext();
        var original = new WallGeometryModel
        {
            WallId = harness.WallId, Json = Wall3DViewBuilderTests.SolvedJson, SchemaVersion = 1, Source = "glyph-solver v1",
            IsActive = false, CreatedAt = captureAt.AddHours(2),
        };
        db.WallGeometryModels.Add(original);
        db.WallGeometryModels.Add(new WallGeometryModel
        {
            WallId = harness.WallId, Json = Wall3DViewBuilderTests.SolvedJson, SchemaVersion = 1, Source = "correction",
            IsActive = true, CreatedAt = DateTimeOffset.UtcNow.AddDays(-1), DerivedFromModelId = original.Id,
        });
        db.WallCaptures.Add(new WallCapture
        {
            WallId = harness.WallId, CreatedByUserId = harness.ActingUser.Id, CreatedAt = captureAt,
            Status = WallCaptureStatus.Succeeded, GeometryModelId = original.Id,
        });
        db.WallPanels.Add(new WallPanel
        {
            WallId = harness.WallId, Col = 0, Row = 0, Generation = 0, Photo = [1, 2, 3], PhotoContentType = "image/jpeg",
            CreatedAt = captureAt + panelAgeAfterCapture,
        });
        await db.SaveChangesAsync();
    }
}
