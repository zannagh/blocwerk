using System.Security.Cryptography;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.Components.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The wall-admin cleanup for photos stored before ingest stripping existed: dry run counts, apply
/// cleans every place a photo lives (wall, panels of every generation, resets, gallery files, journal
/// copies) without touching pixels or orientation, a second run finds nothing, and only a wall admin
/// off-kiosk may run it.
/// </summary>
public class WallPhotoPrivacyServiceTests
{
    [Fact]
    public async Task DryRun_CountsWithoutChanging_ThenApplyCleansEverything_AndIsIdempotent()
    {
        using var h = new WallTestHarness();
        var seeded = await SeedDirtyWallAsync(h);
        var before = await StoredAsync(h, seeded);

        var scan = await Service(h).ScanAsync(h.WallId);

        // Wall photo, live panel, archived panel, staged panel, reset, gallery file, journal copy; the
        // seeded placeholder [1, 2, 3] of the legacy wall photo is counted but has nothing to clean.
        Assert.Equal(new PhotoPrivacyReport(Photos: 7, WithLocation: 6, WithMetadata: 6, Cleaned: 0, Skipped: 0), scan);
        Assert.Equal(before, await StoredAsync(h, seeded));

        var applied = await Service(h).RemoveMetadataAsync(h.WallId);

        Assert.Equal(new PhotoPrivacyReport(7, 6, 6, Cleaned: 6, Skipped: 0), applied);
        var after = await StoredAsync(h, seeded);
        foreach (var (name, bytes) in after.Where(p => p.Key != "wall"))
        {
            Assert.True(bytes.Length < before[name].Length, name);
            PrivacyPhotos.AssertCleanJpeg(before[name], bytes);
        }

        await using (var db = h.CreateContext())
        {
            Assert.Equal(after["gallery"].LongLength, await db.WallImages.Select(i => i.SizeBytes).SingleAsync());
            Assert.Equal(after["journal"].LongLength, await db.JournalBlobs.Select(b => b.Len).SingleAsync());
        }

        var again = await Service(h).RemoveMetadataAsync(h.WallId);
        Assert.Equal(new PhotoPrivacyReport(7, 0, 0, 0, 0), again);
        Assert.Equal(after, await StoredAsync(h, seeded));
    }

    [Fact]
    public async Task AnotherWallsPhotos_AreNeverTouched()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var mine = h.WallId;
        var dirty = PrivacyPhotos.GpsJpeg();
        Guid otherWall;
        await using (var db = h.CreateContext())
        {
            var other = new Wall { Name = "Other", OwnerId = h.Owner.Id, Photo = dirty, PhotoContentType = "image/jpeg" };
            db.Walls.Add(other);
            await db.SaveChangesAsync();
            otherWall = other.Id;
        }

        await Service(h).RemoveMetadataAsync(mine);

        await using var check = h.CreateContext();
        Assert.Equal(dirty, await check.Walls.IgnoreQueryFilters().Where(w => w.Id == otherWall).Select(w => w.Photo).SingleAsync());
    }

    [Fact]
    public async Task AMember_IsRefused()
    {
        using var h = new WallTestHarness();
        await SeedDirtyWallAsync(h);
        h.ActingUser = await h.AddMemberAsync("member@test", WallRole.Member);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(h).ScanAsync(h.WallId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(h).RemoveMetadataAsync(h.WallId));
    }

    [Fact]
    public async Task AKiosk_IsRefused_EvenOnItsOwnWall()
    {
        using var h = new WallTestHarness();
        await SeedDirtyWallAsync(h);
        var kiosk = Substitute.For<IKioskContext>();
        kiosk.IsKiosk.Returns(true);
        kiosk.KioskWallId.Returns(_ => h.WallId);

        await Assert.ThrowsAsync<KioskRestrictedException>(() => Service(h, kiosk).RemoveMetadataAsync(h.WallId));
        await using var db = h.CreateContext();
        Assert.True((await db.WallPanels.Select(p => p.Photo).FirstAsync(p => p != null))!.AsSpan().IndexOf(ExifJpeg.GpsLatitudeBytes) >= 0);
    }

    [Fact]
    public async Task AGalleryRowWhoseSizeLagsItsFile_IsRepairedByTheNextRun_SoItsUrlVersionMoves()
    {
        using var h = new WallTestHarness();
        var seeded = await SeedDirtyWallAsync(h);
        var dirtyLength = (await File.ReadAllBytesAsync(seeded.GalleryPath)).LongLength;
        await Service(h).RemoveMetadataAsync(h.WallId);

        // A crash between the file move and the commit: clean file, the row still on the old size and hash.
        await using (var db = h.CreateContext())
        {
            await db.WallImages.ExecuteUpdateAsync(s => s.SetProperty(i => i.SizeBytes, dirtyLength).SetProperty(i => i.Sha256, new byte[32]));
        }

        var dryRun = await Service(h).ScanAsync(h.WallId);
        Assert.Equal(0, dryRun.WithMetadata);                  // the file is clean: nothing would ever rewrite it
        Assert.Equal(1, dryRun.NeedsRepair);                   // ...but the scan sees the drift
        Assert.True(WallPhotoPrivacy.CanApply(dryRun));        // and the page offers the run that repairs it
        Assert.Equal("Repair", WallPhotoPrivacy.ApplyLabel(dryRun));
        Assert.Contains("1 gallery snapshot needs a repair", WallPhotoPrivacy.ScanText(dryRun));
        await using (var db = h.CreateContext())
        {
            Assert.Equal(dirtyLength, await db.WallImages.Select(i => i.SizeBytes).SingleAsync()); // a dry run never writes
        }

        Assert.Equal(1, (await Service(h).RemoveMetadataAsync(h.WallId)).NeedsRepair);
        var after = await Service(h).ScanAsync(h.WallId);
        Assert.Equal(0, after.NeedsRepair);
        Assert.False(WallPhotoPrivacy.CanApply(after));

        await using var check = h.CreateContext();
        Assert.Equal(new FileInfo(seeded.GalleryPath).Length, await check.WallImages.Select(i => i.SizeBytes).SingleAsync());
        Assert.Equal(SHA256.HashData(await File.ReadAllBytesAsync(seeded.GalleryPath)), await check.WallImages.Select(i => i.Sha256).SingleAsync());
    }

    [Fact]
    public async Task Apply_MovesEveryContentHash_WithTheCleanBytes()
    {
        using var h = new WallTestHarness();
        var seeded = await SeedDirtyWallAsync(h);

        await Service(h).RemoveMetadataAsync(h.WallId);

        var after = await StoredAsync(h, seeded);
        await using var db = h.CreateContext();
        var panels = db.WallPanels.AsNoTracking();
        Assert.Equal(SHA256.HashData(after["live"]), await panels.Where(p => p.Id == seeded.LivePanel).Select(p => p.PhotoSha256).SingleAsync());
        Assert.Equal(SHA256.HashData(after["old"]), await panels.Where(p => p.Id == seeded.OldPanel).Select(p => p.PhotoSha256).SingleAsync());
        Assert.Equal(SHA256.HashData(after["staged"]), await panels.Where(p => p.Id == seeded.StagedPanel).Select(p => p.StagedPhotoSha256).SingleAsync());
        Assert.Equal(SHA256.HashData(after["reset"]), await db.WallResets.Where(r => r.Id == seeded.Reset).Select(r => r.PreviousPhotoSha256).SingleAsync());
        Assert.Equal(SHA256.HashData(after["gallery"]), await db.WallImages.Select(i => i.Sha256).SingleAsync());
    }

    private static WallPhotoPrivacyService Service(WallTestHarness h, IKioskContext? kiosk = null) =>
        new(h.DbContextFactory, h.CurrentUser, h.WallImageStorage, NullLogger<WallPhotoPrivacyService>.Instance, kiosk);

    /// <summary>A wall whose photos all carry GPS, in every place a wall keeps photos.</summary>
    private static async Task<PrivacySeed> SeedDirtyWallAsync(WallTestHarness h)
    {
        await h.SeedWallAsync(holdCount: 0, generation: 2);
        var dirty = PrivacyPhotos.GpsJpeg();
        await using var db = h.CreateContext();
        var live = new WallPanel { WallId = h.WallId, Col = 0, Row = 0, Generation = 2, Photo = dirty, PhotoContentType = "image/jpeg" };
        var old = new WallPanel { WallId = h.WallId, Col = 0, Row = 0, Generation = 1, Photo = dirty, PhotoContentType = "image/jpeg" };
        var staged = new WallPanel { WallId = h.WallId, Col = 1, Row = 0, Generation = 3, StagedPhoto = dirty, StagedAt = DateTimeOffset.UtcNow };
        var reset = new WallReset { WallId = h.WallId, Generation = 1, PreviousPhoto = dirty, PreviousPhotoContentType = "image/jpeg", ResetByUserId = h.Owner.Id };
        db.AddRange(live, old, staged, reset);

        var temp = h.WallImageStorage.CreateTempPath(".jpg");
        await File.WriteAllBytesAsync(temp, dirty);
        var stored = h.WallImageStorage.Commit(temp, ".jpg");
        db.WallImages.Add(new WallImage { WallId = h.WallId, StoragePath = stored, ContentType = "image/jpeg", SizeBytes = dirty.Length });

        // A journal copy of a since-discarded staged panel: only its own after-image names the wall.
        var sha = Convert.ToHexStringLower(SHA256.HashData(dirty));
        db.JournalBlobs.Add(new JournalBlob { Sha256 = sha, Bytes = dirty, Len = dirty.Length });
        var batch = new ChangeJournalBatch { Label = "update" };
        db.ChangeJournalBatches.Add(batch);
        db.ChangeJournalEntries.Add(new ChangeJournalEntry
        {
            BatchId = batch.Id,
            EntityType = nameof(WallPanel),
            KeyJson = $"{{\"Id\":\"{Guid.NewGuid()}\"}}",
            Op = ChangeJournalOp.Insert,
            AfterJson = $"{{\"WallId\":\"{h.WallId}\",\"StagedPhoto\":{{\"$blob\":\"{sha}\",\"len\":{dirty.Length}}}}}",
        });
        await db.SaveChangesAsync();
        return new PrivacySeed(live.Id, old.Id, staged.Id, reset.Id, h.WallImageStorage.ResolvePhysicalPath(stored)!, sha);
    }

    private static async Task<Dictionary<string, byte[]>> StoredAsync(WallTestHarness h, PrivacySeed s)
    {
        await using var db = h.CreateContext();
        var panels = db.WallPanels.AsNoTracking();
        return new Dictionary<string, byte[]>
        {
            ["wall"] = (await db.Walls.IgnoreQueryFilters().Where(w => w.Id == h.WallId).Select(w => w.Photo).SingleAsync())!,
            ["live"] = (await panels.Where(p => p.Id == s.LivePanel).Select(p => p.Photo).SingleAsync())!,
            ["old"] = (await panels.Where(p => p.Id == s.OldPanel).Select(p => p.Photo).SingleAsync())!,
            ["staged"] = (await panels.Where(p => p.Id == s.StagedPanel).Select(p => p.StagedPhoto).SingleAsync())!,
            ["reset"] = (await db.WallResets.Where(r => r.Id == s.Reset).Select(r => r.PreviousPhoto).SingleAsync())!,
            ["gallery"] = await File.ReadAllBytesAsync(s.GalleryPath),
            ["journal"] = await db.JournalBlobs.Where(b => b.Sha256 == s.Sha).Select(b => b.Bytes).SingleAsync(),
        };
    }
}

/// <summary>Where <see cref="WallPhotoPrivacyServiceTests"/> put the wall's dirty photos.</summary>
internal sealed record PrivacySeed(Guid LivePanel, Guid OldPanel, Guid StagedPanel, Guid Reset, string GalleryPath, string Sha);
