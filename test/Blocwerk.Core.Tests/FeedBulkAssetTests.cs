// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Capture.Retention;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using static Blocwerk.Core.Tests.FeedBulkKit;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The asset bulk sites (PR 1.3): the photo-privacy rewrites move a photo's hash, so each appends a <c>wall.manifest</c>
/// (editors only for a staged photo) or a <c>gallery.changed</c>; stripping retired 3D models appends a
/// <c>view3d.changed</c>.
/// </summary>
public sealed class FeedBulkAssetTests
{
    [Fact]
    public async Task RemovingPhotoMetadata_WritesAManifestPerPhoto_AndAGalleryRowPerUpload()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: 1);
        var dirty = PrivacyPhotos.GpsJpeg();
        Guid image;
        await using (var db = h.CreateContext())
        {
            db.WallPanels.Add(new WallPanel { WallId = h.WallId, Col = 0, Row = 0, Generation = 1, Photo = dirty, PhotoContentType = "image/jpeg" });
            db.WallPanels.Add(new WallPanel { WallId = h.WallId, Col = 1, Row = 0, Generation = 2, StagedPhoto = dirty, StagedAt = DateTimeOffset.UtcNow });
            var temp = h.WallImageStorage.CreateTempPath(".jpg");
            await File.WriteAllBytesAsync(temp, dirty);
            var upload = new WallImage { WallId = h.WallId, StoragePath = h.WallImageStorage.Commit(temp, ".jpg"), ContentType = "image/jpeg", SizeBytes = dirty.Length };
            db.WallImages.Add(upload);
            await db.SaveChangesAsync();
            image = upload.Id;
        }

        await ClearAsync(h);
        var service = new WallPhotoPrivacyService(h.DbContextFactory, h.CurrentUser, h.WallImageStorage, NullLogger<WallPhotoPrivacyService>.Instance);
        Assert.Equal(3, (await service.RemoveMetadataAsync(h.WallId)).Cleaned);

        var rows = await RowsAsync(h);
        Assert.All(rows, r => Assert.Equal(h.WallId, r.WallId));
        Assert.Equal(
            [(FeedKinds.WallManifest, FeedAudience.Wall), (FeedKinds.WallManifest, FeedAudience.Setters), (FeedKinds.GalleryChanged, FeedAudience.Wall)],
            rows.Select(r => (r.Kind, r.Audience)));
        Assert.Equal(image, rows[^1].EntityId);

        // A second run finds nothing to clean and writes nothing.
        await ClearAsync(h);
        await service.RemoveMetadataAsync(h.WallId);
        Assert.Empty(await RowsAsync(h));
    }

    [Fact]
    public async Task StrippingARetiredModel_WritesView3DChanged()
    {
        using var h = new WallTestHarness();
        using var s = await SupersededModelRetentionTests.ScenarioAsync(h);
        var glyphs = WallGlyphSettingsTests.Service(h);
        await glyphs.ImportGeometryAsync(h.WallId, GlyphGeometryJson.Build(), null);
        var stored = (await glyphs.ImportGeometryAsync(h.WallId, GlyphGeometryJson.Build(), null, null, new GeometryImportOptions(Activate: false))).Model!.Id;
        await ModelFilesTestData.AddAsync(h, s.Files, stored);
        await using (var age = h.CreateContext())
        {
            await age.WallGeometryModels.Where(m => m.Id == stored)
                .ExecuteUpdateAsync(x => x.SetProperty(m => m.CreatedAt, DateTimeOffset.UtcNow.AddDays(-40)));
        }

        await ClearAsync(h);
        await using var db = h.CreateContext();
        var candidates = await SupersededModelSelection.SelectAsync(db, 1, s.Options, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(1, await SupersededModelRetention.StripWallAsync(db, h.WallId, candidates, 1, s.Options, DateTimeOffset.UtcNow, [], CancellationToken.None));

        var row = Assert.Single(await RowsAsync(h));
        Assert.Equal((FeedKinds.View3DChanged, h.WallId, stored), (row.Kind, row.WallId!.Value, row.EntityId!.Value));
    }
}
