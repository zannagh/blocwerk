// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Retired models' textures and photo-real views: the newest retired model per wall (and anything retired within the
/// grace) keeps them for a revert, the active model's family and models awaiting an admin's decision are spared, a busy
/// wall is left alone, a file another row shares stays, and the stripped model keeps its geometry and says so.
/// </summary>
public class SupersededModelRetentionTests
{
    [Fact]
    public async Task KeepsTheNewestRetiredModel_StripsOlderOnes_AndKeepsTheirGeometry()
    {
        using var h = new WallTestHarness();
        using var s = await ScenarioAsync(h);
        var (a, b, c) = await ImportThreeAsync(h);
        var aFiles = await ModelFilesTestData.AddAsync(h, s.Files, a);
        var bFiles = await ModelFilesTestData.AddAsync(h, s.Files, b);
        var cFiles = await ModelFilesTestData.AddAsync(h, s.Files, c);
        await RetireAsync(h, a, days: 60);
        await RetireAsync(h, b, days: 30);

        var result = await Sweeper(s).SweepAsync(CancellationToken.None);

        Assert.Equal(1, result.SupersededModels.Count);
        Assert.True(result.SupersededModels.Bytes > 0);
        Assert.True(result.FreedBytes >= result.SupersededModels.Bytes);
        Assert.All(aFiles, f => Assert.False(Exists(s, f)));
        Assert.All(bFiles.Concat(cFiles), f => Assert.True(Exists(s, f)));
        await using (var db = h.CreateContext())
        {
            Assert.Equal(3, await db.WallGeometryModels.CountAsync());
            Assert.False(await db.WallGeometryTextures.AnyAsync(t => t.GeometryModelId == a));
            Assert.False(await db.WallGeometrySplats.AnyAsync(t => t.GeometryModelId == a));
            Assert.Equal(2, await db.WallGeometrySplats.CountAsync());
        }

        var history = await WallGlyphSettingsTests.Service(h).GetGeometryHistoryAsync(h.WallId);
        Assert.NotNull(history.Single(e => e.Id == a).FilesRemovedAt);
        Assert.All(history.Where(e => e.Id != a), e => Assert.Null(e.FilesRemovedAt));

        // Idempotent: nothing left to do.
        Assert.Equal(0, (await Sweeper(s).SweepAsync(CancellationToken.None)).SupersededModels.Count);
    }

    [Fact]
    public async Task RecentlyRetiredModels_KeepTheirFiles_ThroughTheGrace()
    {
        using var h = new WallTestHarness();
        using var s = await ScenarioAsync(h);
        var (a, b, _) = await ImportThreeAsync(h);
        var aFiles = await ModelFilesTestData.AddAsync(h, s.Files, a);
        await RetireAsync(h, a, days: 3);
        await RetireAsync(h, b, days: 2);

        var result = await Sweeper(s).SweepAsync(CancellationToken.None);

        Assert.Equal(0, result.SupersededModels.Count);
        Assert.All(aFiles, f => Assert.True(Exists(s, f)));
    }

    [Fact]
    public async Task AFileAKeptModelShares_StaysOnDisk()
    {
        using var h = new WallTestHarness();
        using var s = await ScenarioAsync(h);
        var (a, b, c) = await ImportThreeAsync(h);
        var aFiles = await ModelFilesTestData.AddAsync(h, s.Files, a);
        await ModelFilesTestData.AddAsync(h, s.Files, b);
        await RetireAsync(h, a, days: 60);
        await RetireAsync(h, b, days: 30);
        await using (var db = h.CreateContext())
        {
            // A correction shares its parent's texture files: here the active model points at one of a's.
            var texture = await db.WallGeometryTextures.AsNoTracking().FirstAsync(t => t.GeometryModelId == a);
            db.WallGeometryTextures.Add(new WallGeometryTexture { GeometryModelId = c, FacetId = "0", StoredPath = texture.StoredPath });
            await db.SaveChangesAsync();
        }

        await Sweeper(s).SweepAsync(CancellationToken.None);

        Assert.True(Exists(s, aFiles[0]));
        Assert.All(aFiles.Skip(1), f => Assert.False(Exists(s, f)));
    }

    [Fact]
    public async Task TheActiveModelsFamily_IsNeverStripped()
    {
        using var h = new WallTestHarness();
        using var s = await ScenarioAsync(h, keep: 0);
        var (_, b, c) = await ImportThreeAsync(h);
        var bFiles = await ModelFilesTestData.AddAsync(h, s.Files, b);
        await RetireAsync(h, b, days: 90);
        await using (var db = h.CreateContext())
        {
            // c is a correction of b: reverting to b is the correction's undo.
            await db.WallGeometryModels.Where(m => m.Id == c).ExecuteUpdateAsync(x => x.SetProperty(m => m.DerivedFromModelId, b));
        }

        await Sweeper(s).SweepAsync(CancellationToken.None);

        Assert.All(bFiles, f => Assert.True(Exists(s, f)));
    }

    [Fact]
    public async Task AWallWithACaptureInFlight_IsLeftAlone()
    {
        using var h = new WallTestHarness();
        using var s = await ScenarioAsync(h, keep: 0);
        var (a, _, _) = await ImportThreeAsync(h);
        var aFiles = await ModelFilesTestData.AddAsync(h, s.Files, a);
        await RetireAsync(h, a, days: 60);
        await using (var db = h.CreateContext())
        {
            db.WallCaptures.Add(new WallCapture { WallId = h.WallId, CreatedByUserId = h.ActingUser.Id, Status = WallCaptureStatus.Texturing });
            await db.SaveChangesAsync();
        }

        Assert.Equal(0, (await Sweeper(s).SweepAsync(CancellationToken.None)).SupersededModels.Count);
        Assert.All(aFiles, f => Assert.True(Exists(s, f)));
    }

    [Fact]
    public async Task AModelAwaitingADecision_DoesNotCount_AndKeepsItsFilesAsLongAsItsPhotos()
    {
        using var h = new WallTestHarness();
        using var s = await ScenarioAsync(h);
        var (a, b, _) = await ImportThreeAsync(h);
        await RetireAsync(h, a, days: 60);
        await RetireAsync(h, b, days: 30);
        var bFiles = await ModelFilesTestData.AddAsync(h, s.Files, b);
        var glyphs = WallGlyphSettingsTests.Service(h);
        var pending = (await glyphs.ImportGeometryAsync(h.WallId, GlyphGeometryJson.Build(), null, "capture", new GeometryImportOptions(Activate: false))).Model!.Id;
        var pendingFiles = await ModelFilesTestData.AddAsync(h, s.Files, pending);
        await using (var db = h.CreateContext())
        {
            db.WallCaptures.Add(new WallCapture
            {
                WallId = h.WallId, CreatedByUserId = h.ActingUser.Id, Status = WallCaptureStatus.StoredNotActivated, GeometryModelId = pending,
            });
            await db.SaveChangesAsync();
        }

        await AgeAsync(h, pending, days: 20);
        await Sweeper(s).SweepAsync(CancellationToken.None);
        Assert.All(pendingFiles, f => Assert.True(Exists(s, f)));
        Assert.All(bFiles, f => Assert.True(Exists(s, f))); // the newer pending model does not take b's place

        await AgeAsync(h, pending, days: 40);
        var result = await Sweeper(s).SweepAsync(CancellationToken.None);
        Assert.Equal(1, result.SupersededModels.Count);
        Assert.All(pendingFiles, f => Assert.False(Exists(s, f)));
    }

    [Fact]
    public async Task DryRun_ChangesNothing_ButSaysWhatItWouldFree()
    {
        using var h = new WallTestHarness();
        using var s = await ScenarioAsync(h, keep: 0, dryRun: true);
        var (a, b, _) = await ImportThreeAsync(h);
        var files = (await ModelFilesTestData.AddAsync(h, s.Files, a)).Concat(await ModelFilesTestData.AddAsync(h, s.Files, b)).ToList();
        await RetireAsync(h, a, days: 60);
        await RetireAsync(h, b, days: 30);

        var result = await Sweeper(s).SweepAsync(CancellationToken.None);

        Assert.Equal(2, result.SupersededModels.Count);
        Assert.Equal(files.Sum(f => new FileInfo(s.Files.ResolvePhysicalPath(f)!).Length), result.SupersededModels.Bytes);
        Assert.All(files, f => Assert.True(Exists(s, f)));
        await using var db = h.CreateContext();
        Assert.Equal(4, await db.WallGeometrySplats.CountAsync() + await db.WallGeometryTextures.CountAsync());
        Assert.False(await db.WallGeometryModels.AnyAsync(m => m.FilesRemovedAt != null));
    }

    [Fact]
    public async Task KeepAll_StripsNothing()
    {
        using var h = new WallTestHarness();
        using var s = await ScenarioAsync(h, keep: null);
        var (a, b, _) = await ImportThreeAsync(h);
        await ModelFilesTestData.AddAsync(h, s.Files, a);
        await RetireAsync(h, a, days: 600);
        await RetireAsync(h, b, days: 300);

        Assert.Equal(0, (await Sweeper(s).SweepAsync(CancellationToken.None)).SupersededModels.Count);
    }

    internal static async Task<CaptureScenario> ScenarioAsync(WallTestHarness h, int? keep = 1, bool dryRun = false)
    {
        await h.SeedWallAsync(holdCount: 0);
        return new CaptureScenario(h) { Options = new WallCapturePipelineOptions { KeepSupersededModels = keep, RetentionDryRun = dryRun } };
    }

    internal static WallCaptureSweeper Sweeper(CaptureScenario s) =>
        new(s.Harness.RootContextFactory, s.Files, s.Options, NullLogger<WallCaptureSweeper>.Instance);

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

    private static async Task RetireAsync(WallTestHarness h, Guid modelId, int days)
    {
        await using var db = h.CreateContext();
        var model = await db.WallGeometryModels.SingleAsync(m => m.Id == modelId);
        model.RetiredAt = DateTimeOffset.UtcNow.AddDays(-days);
        model.CreatedAt = DateTimeOffset.UtcNow.AddDays(-days - 1);
        await db.SaveChangesAsync();
    }

    private static async Task AgeAsync(WallTestHarness h, Guid modelId, int days)
    {
        await using var db = h.CreateContext();
        (await db.WallGeometryModels.SingleAsync(m => m.Id == modelId)).CreatedAt = DateTimeOffset.UtcNow.AddDays(-days);
        await db.SaveChangesAsync();
    }

    private static bool Exists(CaptureScenario s, string name) => File.Exists(s.Files.ResolvePhysicalPath(name));
}
