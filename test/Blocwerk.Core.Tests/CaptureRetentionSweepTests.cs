using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Wall photos can show people, so capture files are not kept without a reason: stale drafts go, photos
/// of old captures go after the retention period unless they produced the ACTIVE model, and files whose
/// rows were cascaded away (a deleted wall) are removed from disk.
/// </summary>
public class CaptureRetentionSweepTests
{
    [Fact]
    public async Task ExpiredPhotos_AreDeleted_ExceptThoseOfTheActiveModel()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var active = await s.StartCaptureAsync();
        await s.Processor.ProcessAsync(active, CancellationToken.None);
        var old = await AddEndedCaptureAsync(h, s, WallCaptureStatus.Failed, DateTimeOffset.UtcNow.AddDays(-31));
        var recent = await AddEndedCaptureAsync(h, s, WallCaptureStatus.Failed, DateTimeOffset.UtcNow.AddDays(-2));
        await AgeAsync(h, active, DateTimeOffset.UtcNow.AddDays(-90));
        var oldFile = await PhotoPathAsync(h, old);

        var result = await Sweeper(s).SweepAsync(CancellationToken.None);

        Assert.Equal(1, result.ExpiredPhotos);
        await using var db = h.CreateContext();
        Assert.Equal(2, await db.WallCapturePhotos.CountAsync(p => p.CaptureId == active));
        Assert.Equal(1, await db.WallCapturePhotos.CountAsync(p => p.CaptureId == recent));
        Assert.False(await db.WallCapturePhotos.AnyAsync(p => p.CaptureId == old));
        Assert.False(File.Exists(s.Files.ResolvePhysicalPath(oldFile)));
        Assert.True(await db.WallCaptures.AnyAsync(c => c.Id == old)); // the history row stays
    }

    [Fact]
    public async Task OrphanFiles_OfADeletedWall_AreRemoved_ButNotFreshOrForeignOnes()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var captureId = await s.StartCaptureAsync();
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);
        var kept = await PhotoPathAsync(h, captureId);
        var orphan = await s.Files.SaveAsync(CaptureScenario.TinyJpeg(9), ".jpg", CancellationToken.None);
        var fresh = await s.Files.SaveAsync(CaptureScenario.TinyJpeg(8), ".png", CancellationToken.None);
        var foreign = await s.Files.SaveAsync([1, 2, 3], ".ply", CancellationToken.None);
        Backdate(s, orphan);
        Backdate(s, foreign);
        Backdate(s, kept);

        var result = await Sweeper(s).SweepAsync(CancellationToken.None);

        Assert.Equal(1, result.OrphanFiles);
        Assert.False(File.Exists(s.Files.ResolvePhysicalPath(orphan)));
        Assert.True(File.Exists(s.Files.ResolvePhysicalPath(kept)));
        Assert.True(File.Exists(s.Files.ResolvePhysicalPath(fresh)));
        Assert.True(File.Exists(s.Files.ResolvePhysicalPath(foreign)));

        // Deleting the wall cascades every capture row; the next sweep takes the files with it.
        await using (var db = h.CreateContext())
        {
            db.Walls.Remove(await db.Walls.SingleAsync());
            await db.SaveChangesAsync();
        }

        var textures = s.Files.ListFiles().Where(f => f.Name != fresh && f.Name != foreign).ToList();
        textures.ForEach(f => Backdate(s, f.Name));
        await Sweeper(s).SweepAsync(CancellationToken.None);
        Assert.Equal(new[] { fresh, foreign }.Order(), s.Files.ListFiles().Select(f => f.Name).Order());
    }

    [Fact]
    public async Task StaleDrafts_AreSwept_Periodically()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        await h.SeedWallAsync(holdCount: 0);
        await WallGlyphSettingsTests.Service(h).SetGlyphSettingsAsync(h.WallId, true, 125);
        var draft = await s.Service.CreateDraftAsync(h.WallId);
        await s.Service.AddPhotoAsync(draft.CaptureId, "a.jpg", CaptureScenario.TinyJpeg(), CancellationToken.None);
        var stored = await PhotoPathAsync(h, draft.CaptureId);
        await using (var db = h.CreateContext())
        {
            (await db.WallCaptures.SingleAsync()).CreatedAt = DateTimeOffset.UtcNow.AddDays(-2);
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, (await Sweeper(s).SweepAsync(CancellationToken.None)).Drafts);
        Assert.False(File.Exists(s.Files.ResolvePhysicalPath(stored)));
    }

    [Theory]
    [InlineData("7", 7)]
    [InlineData("0", null)]
    [InlineData(null, 30)]
    public void Retention_IsASetting(string? days, int? expected)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Blocwerk:Capture:PhotoRetentionDays"] = days })
            .Build();

        Assert.Equal(expected, WallCapturePipelineOptions.Bind(config).PhotoRetention?.Days);
    }

    private static WallCaptureSweeper Sweeper(CaptureScenario s) =>
        new(s.Harness.RootContextFactory, s.Files, s.Options, NullLogger<WallCaptureSweeper>.Instance);

    private static void Backdate(CaptureScenario s, string name) =>
        File.SetLastWriteTimeUtc(s.Files.ResolvePhysicalPath(name)!, DateTime.UtcNow.AddHours(-3));

    private static async Task<Guid> AddEndedCaptureAsync(
        WallTestHarness h, CaptureScenario s, WallCaptureStatus status, DateTimeOffset completedAt)
    {
        await using var db = h.CreateContext();
        var capture = new WallCapture
        {
            WallId = h.WallId, CreatedByUserId = h.ActingUser.Id, Status = status, CompletedAt = completedAt,
        };
        db.WallCaptures.Add(capture);
        db.WallCapturePhotos.Add(new WallCapturePhoto
        {
            CaptureId = capture.Id,
            Index = 1,
            StoredPath = await s.Files.SaveAsync(CaptureScenario.TinyJpeg(7), ".jpg", CancellationToken.None),
            ContentHash = Guid.NewGuid().ToString("N"),
        });
        await db.SaveChangesAsync();
        return capture.Id;
    }

    private static async Task AgeAsync(WallTestHarness h, Guid captureId, DateTimeOffset completedAt)
    {
        await using var db = h.CreateContext();
        (await db.WallCaptures.SingleAsync(c => c.Id == captureId)).CompletedAt = completedAt;
        await db.SaveChangesAsync();
    }

    private static async Task<string> PhotoPathAsync(WallTestHarness h, Guid captureId)
    {
        await using var db = h.CreateContext();
        return await db.WallCapturePhotos.Where(p => p.CaptureId == captureId).Select(p => p.StoredPath).FirstAsync();
    }
}
