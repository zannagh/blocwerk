using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Covers the sensor-facing telemetry: temperature samples are server-stamped and query by range,
/// and the image gallery merges uploads with the legacy photos that still live in the database.
/// </summary>
public class WallTelemetryTests
{
    [Fact]
    public async Task RecordReading_StampsServerTime_AndQueriesByRange()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var first = await h.WallTemperatureService.RecordReadingAsync(h.WallId, 18.5);
        var second = await h.WallTemperatureService.RecordReadingAsync(h.WallId, 21.25);
        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        Assert.InRange(first.RecordedAt, before, after);

        var inRange = await h.WallTemperatureService.GetReadingsAsync(h.WallId, before, after, 100);
        Assert.False(inRange.Truncated);
        Assert.Equal([first.Id, second.Id], inRange.Readings.Select(r => r.Id));
        Assert.Equal(21.25, inRange.Readings[1].TemperatureCelsius);

        var empty = await h.WallTemperatureService.GetReadingsAsync(h.WallId, after, after.AddHours(1), 100);
        Assert.Empty(empty.Readings);

        var latest = await h.WallTemperatureService.GetLatestReadingAsync(h.WallId);
        Assert.NotNull(latest);
        Assert.Equal(second.Id, latest.Id);
    }

    [Fact]
    public async Task RecordReading_RejectsUnknownWall()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.WallTemperatureService.RecordReadingAsync(Guid.NewGuid(), 20));
    }

    [Fact]
    public async Task Gallery_MergesUploadsWithWallAndResetPhotos()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        var resetId = await SeedResetPhotoAsync(h);

        var upload = await h.WallImageService.RecordImageAsync(
            h.WallId,
            "shot.jpg",
            "image/jpeg",
            42,
            "After the reset",
            DateTimeOffset.UtcNow);

        var gallery = await h.WallImageService.GetGalleryAsync(h.WallId);

        Assert.Equal(3, gallery.Count);
        Assert.Equal(upload.Id, gallery[0].Id);
        Assert.Equal(WallGallerySource.Uploaded, gallery[0].Source);
        Assert.Contains(gallery, i => i.Source == WallGallerySource.WallPhoto && i.Id == h.WallId);
        Assert.Contains(gallery, i => i.Source == WallGallerySource.ResetPhoto && i.Id == resetId);

        // Newest first, and paging walks that same order.
        Assert.True(gallery[0].CapturedAt >= gallery[1].CapturedAt);
        var page = await h.WallImageService.GetGalleryAsync(h.WallId, skip: 1, take: 1);
        Assert.Single(page);
        Assert.Equal(gallery[1].Id, page[0].Id);

        // The legacy blobs are readable through the dedicated bytes call, and never copied.
        var wallPhoto = await h.WallImageService.GetLegacyImageContentAsync(h.WallId, WallGallerySource.WallPhoto, h.WallId);
        Assert.NotNull(wallPhoto);
        Assert.Equal([1, 2, 3], wallPhoto.Data);

        var resetPhoto = await h.WallImageService.GetLegacyImageContentAsync(h.WallId, WallGallerySource.ResetPhoto, resetId);
        Assert.NotNull(resetPhoto);
        Assert.Equal([9, 9], resetPhoto.Data);

        // Uploads are files, so they are not served through the legacy path.
        Assert.Null(await h.WallImageService.GetLegacyImageContentAsync(h.WallId, WallGallerySource.Uploaded, upload.Id));

        await using var db = h.CreateContext();
        Assert.Equal(1, await db.WallImages.CountAsync());
    }

    [Fact]
    public async Task DeleteImage_RequiresWallAdmin()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var member = await h.AddMemberAsync("member@test", WallRole.Member);

        var tempPath = h.WallImageStorage.CreateTempPath(".jpg");
        await File.WriteAllBytesAsync(tempPath, [1, 2, 3, 4]);
        var storedName = h.WallImageStorage.Commit(tempPath, ".jpg");

        var image = await h.WallImageService.RecordImageAsync(h.WallId, storedName, "image/jpeg", 4);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => h.WallImageService.DeleteImageAsync(image.Id, member.Id));

        Assert.NotNull(await h.WallImageService.GetImageAsync(image.Id));

        await h.WallImageService.DeleteImageAsync(image.Id, h.Owner.Id);

        Assert.Null(await h.WallImageService.GetImageAsync(image.Id));
        Assert.False(File.Exists(h.WallImageStorage.ResolvePhysicalPath(storedName)!));
    }

    private static async Task<Guid> SeedResetPhotoAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var reset = new Blocwerk.Core.Entities.WallReset
        {
            WallId = h.WallId,
            Generation = 0,
            PreviousPhoto = [9, 9],
            PreviousPhotoContentType = "image/png",
            ResetAt = DateTimeOffset.UtcNow.AddDays(-2),
            ResetByUserId = h.Owner.Id,
        };
        db.WallResets.Add(reset);
        await db.SaveChangesAsync();
        return reset.Id;
    }
}
