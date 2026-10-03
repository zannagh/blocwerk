// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json.Nodes;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Capture.Retention;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Migrations;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Blocwerk.Core.Tests;

/// <summary>
/// What keeps the retention of retired models from taking the wrong one: only families that were active and still have
/// files take a revert slot, the models a kept one carries textures from stay, existing models get the grace once after
/// the migration, and a wall that changed between the selection and the strip is left alone.
/// </summary>
public class SupersededModelRetentionGuardTests
{
    [Fact]
    public async Task ARetiredModelWithoutFiles_DoesNotTakeTheRevertSlot()
    {
        using var h = new WallTestHarness();
        using var s = await SupersededModelRetentionTests.ScenarioAsync(h);
        var (a, b, _) = await ImportThreeAsync(h);
        var aFiles = await ModelFilesTestData.AddAsync(h, s.Files, a);
        await RetireAsync(h, a, days: 60);
        await RetireAsync(h, b, days: 30); // an upload without textures: activating it gives no 3D view back anyway

        Assert.Equal(0, (await Sweep(s)).SupersededModels.Count);
        Assert.All(aFiles, f => Assert.True(Exists(s, f)));
    }

    [Fact]
    public async Task ANeverActiveModel_DoesNotTakeTheRevertSlot_AndGoesAfterThePhotoRetention()
    {
        using var h = new WallTestHarness();
        using var s = await SupersededModelRetentionTests.ScenarioAsync(h);
        var (a, _, _) = await ImportThreeAsync(h);
        var aFiles = await ModelFilesTestData.AddAsync(h, s.Files, a);
        await RetireAsync(h, a, days: 60);
        var glyphs = WallGlyphSettingsTests.Service(h);
        var stored = (await glyphs.ImportGeometryAsync(h.WallId, GlyphGeometryJson.Build(), null, null, new GeometryImportOptions(Activate: false))).Model!.Id;
        var storedFiles = await ModelFilesTestData.AddAsync(h, s.Files, stored);

        Assert.Equal(0, (await Sweep(s)).SupersededModels.Count);

        await SetAsync(h, stored, retiredDaysAgo: null, createdDaysAgo: 40);
        Assert.Equal(1, (await Sweep(s)).SupersededModels.Count);
        Assert.All(storedFiles, f => Assert.False(Exists(s, f)));
        Assert.All(aFiles, f => Assert.True(Exists(s, f)));
    }

    [Fact]
    public async Task TheModelsTheActiveAndTheKeptModelCarryTexturesFrom_AreKept()
    {
        using var h = new WallTestHarness();
        using var s = await SupersededModelRetentionTests.ScenarioAsync(h);
        var glyphs = WallGlyphSettingsTests.Service(h);
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add((await glyphs.ImportGeometryAsync(h.WallId, GlyphGeometryJson.Build(), null)).Model!.Id);
        }

        // ids[4] is active and carries from ids[0]; ids[3] (the kept revert target) carries from ids[1]; ids[2] goes.
        var files = new List<List<string>>();
        for (var i = 0; i < 4; i++)
        {
            files.Add(await ModelFilesTestData.AddAsync(h, s.Files, ids[i]));
            await SetAsync(h, ids[i], retiredDaysAgo: 90 - (i * 10), createdDaysAgo: 100 - (i * 10));
        }

        await CarryAsync(h, ids[4], from: ids[0]);
        await CarryAsync(h, ids[3], from: ids[1]);

        var result = await Sweep(s);

        Assert.Equal(1, result.SupersededModels.Count);
        Assert.All(files[2], f => Assert.False(Exists(s, f)));
        Assert.All(files[0].Concat(files[1]).Concat(files[3]), f => Assert.True(Exists(s, f)));
    }

    [Fact]
    public async Task TheMigration_GivesEveryExistingInactiveModelTheGraceOnce()
    {
        var sql = new AddGeometryModelRetention().UpOperations.OfType<SqlOperation>().Single().Sql;
        Assert.Equal(AddGeometryModelRetention.BackfillRetiredAtSql, sql);
        Assert.Contains("SET \"RetiredAt\" = now()", sql);
        Assert.Contains("WHERE NOT \"IsActive\" AND \"RetiredAt\" IS NULL", sql);

        // What the backfill leaves: old models retired "now" keep their files through the grace, even with keep = 0...
        using var h = new WallTestHarness();
        using var s = await SupersededModelRetentionTests.ScenarioAsync(h, keep: 0);
        var (a, b, _) = await ImportThreeAsync(h);
        var aFiles = await ModelFilesTestData.AddAsync(h, s.Files, a);
        var bFiles = await ModelFilesTestData.AddAsync(h, s.Files, b);
        await SetAsync(h, a, retiredDaysAgo: 0, createdDaysAgo: 400);
        await SetAsync(h, b, retiredDaysAgo: 0, createdDaysAgo: 300);
        Assert.Equal(0, (await Sweep(s)).SupersededModels.Count);

        // ...and once it is over, a tie goes to the newer model (the likelier revert target).
        s.Options = new WallCapturePipelineOptions { KeepSupersededModels = 1, RetentionDryRun = false };
        await SetAsync(h, a, retiredDaysAgo: 20, createdDaysAgo: 400);
        await SetAsync(h, b, retiredDaysAgo: 20, createdDaysAgo: 300);
        Assert.Equal(1, (await Sweep(s)).SupersededModels.Count);
        Assert.All(aFiles, f => Assert.False(Exists(s, f)));
        Assert.All(bFiles, f => Assert.True(Exists(s, f)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AWallThatChangedAfterTheSelection_IsLeftAlone(bool activated)
    {
        using var h = new WallTestHarness();
        using var s = await SupersededModelRetentionTests.ScenarioAsync(h, keep: 0);
        var (a, _, c) = await ImportThreeAsync(h);
        var aFiles = await ModelFilesTestData.AddAsync(h, s.Files, a);
        await RetireAsync(h, a, days: 60);
        await using var db = h.CreateContext();
        var candidates = await SupersededModelSelection.SelectAsync(db, 0, s.Options, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(new[] { a }, candidates);

        if (activated)
        {
            // An admin activates the candidate meanwhile.
            await db.WallGeometryModels.Where(m => m.Id == c).ExecuteUpdateAsync(x => x.SetProperty(m => m.IsActive, false));
            await db.WallGeometryModels.Where(m => m.Id == a).ExecuteUpdateAsync(x => x.SetProperty(m => m.IsActive, true));
        }
        else
        {
            // A capture starts on the wall meanwhile.
            db.WallCaptures.Add(new WallCapture { WallId = h.WallId, CreatedByUserId = h.ActingUser.Id, Status = WallCaptureStatus.Queued });
            await db.SaveChangesAsync();
        }

        var removed = new List<string>();
        var stripped = await SupersededModelRetention.StripWallAsync(
            db, h.WallId, candidates, 0, s.Options, DateTimeOffset.UtcNow, removed, CancellationToken.None);

        Assert.Equal(0, stripped);
        Assert.Empty(removed);
        Assert.True(await db.WallGeometryTextures.AnyAsync(t => t.GeometryModelId == a));
        Assert.Null((await db.WallGeometryModels.AsNoTracking().SingleAsync(m => m.Id == a)).FilesRemovedAt);
        Assert.All(aFiles, f => Assert.True(Exists(s, f)));
    }

    private static Task<CaptureSweepResult> Sweep(CaptureScenario s) =>
        SupersededModelRetentionTests.Sweeper(s).SweepAsync(CancellationToken.None);

    private static async Task<(Guid A, Guid B, Guid C)> ImportThreeAsync(WallTestHarness h)
    {
        var glyphs = WallGlyphSettingsTests.Service(h);
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add((await glyphs.ImportGeometryAsync(h.WallId, GlyphGeometryJson.Build(), null)).Model!.Id);
        }

        return (ids[0], ids[1], ids[2]);
    }

    private static Task RetireAsync(WallTestHarness h, Guid modelId, int days) => SetAsync(h, modelId, days, days + 1);

    private static async Task SetAsync(WallTestHarness h, Guid modelId, int? retiredDaysAgo, int createdDaysAgo)
    {
        await using var db = h.CreateContext();
        var model = await db.WallGeometryModels.SingleAsync(m => m.Id == modelId);
        model.RetiredAt = retiredDaysAgo is { } r ? DateTimeOffset.UtcNow.AddDays(-r) : null;
        model.CreatedAt = DateTimeOffset.UtcNow.AddDays(-createdDaysAgo);
        await db.SaveChangesAsync();
    }

    /// <summary>Makes <paramref name="modelId"/> a partial re-capture registered to <paramref name="from"/>, carrying facet "0".</summary>
    private static async Task CarryAsync(WallTestHarness h, Guid modelId, Guid from)
    {
        await using var db = h.CreateContext();
        var model = await db.WallGeometryModels.SingleAsync(m => m.Id == modelId);
        var json = JsonNode.Parse(model.Json)!.AsObject();
        if (json["quality"] is not JsonObject quality)
        {
            quality = [];
            json["quality"] = quality;
        }

        quality["registration"] = new JsonObject { ["referenceModelId"] = from.ToString(), ["carriedFacets"] = new JsonArray("0") };
        model.Json = json.ToJsonString();
        await db.SaveChangesAsync();
    }

    private static bool Exists(CaptureScenario s, string name) => File.Exists(s.Files.ResolvePhysicalPath(name));
}
