// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Cryptography;
using Blocwerk.Core.Assets;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Capture.Replay;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The writers of stored-file assets hash what they store: gallery uploads (streamed from the stored file), textures and
/// masks, the photo-real scene and its level-of-detail files, and imported textures (from the package's checked file list).
/// </summary>
public class ContentHashWriterTests
{
    [Fact]
    public async Task OfFile_StreamsTheSameHash_AndIsNullWithoutAFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"blocwerk-hash-{Guid.NewGuid():N}.bin");
        var bytes = RandomNumberGenerator.GetBytes(300_000);
        await File.WriteAllBytesAsync(path, bytes);
        try
        {
            Assert.Equal(SHA256.HashData(bytes), await ContentHash.OfFileAsync(path, default));
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Null(await ContentHash.OfFileAsync(path, default));
        Assert.Null(await ContentHash.OfFileAsync(null, default));
        Assert.Equal(SHA256.HashData(bytes), ContentHash.FromHex(ContentHash.ToHex(SHA256.HashData(bytes))));
        Assert.Null(ContentHash.FromHex("not a hash"));
    }

    [Fact]
    public async Task AGalleryUpload_IsHashedFromItsStoredFile()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var temp = h.WallImageStorage.CreateTempPath(".jpg");
        await File.WriteAllBytesAsync(temp, [1, 2, 3, 4]);
        var stored = h.WallImageStorage.Commit(temp, ".jpg");

        var image = await h.WallImageService.RecordImageAsync(h.WallId, stored, "image/jpeg", 4);
        var orphan = await h.WallImageService.RecordImageAsync(h.WallId, "missing.jpg", "image/jpeg", 4);

        await using var db = h.CreateContext();
        Assert.Equal(SHA256.HashData([1, 2, 3, 4]), await db.WallImages.Where(i => i.Id == image.Id).Select(i => i.Sha256).SingleAsync());
        Assert.Null(await db.WallImages.Where(i => i.Id == orphan.Id).Select(i => i.Sha256).SingleAsync());
    }

    [Fact]
    public async Task TexturesMasksAndTheScene_AreHashedAsStored()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.SplatClient.IsConfigured = true;
        var captureId = await s.StartCaptureAsync();

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using var db = h.CreateContext();
        var textures = await db.WallGeometryTextures.ToListAsync();
        Assert.Equal(2, textures.Count);
        foreach (var texture in textures)
        {
            Assert.Equal(await FileHashAsync(s, texture.StoredPath), texture.Sha256);
            Assert.Equal(texture.MaskStoredPath is { } mask ? await FileHashAsync(s, mask) : null, texture.MaskSha256);
        }

        Assert.Contains(textures, t => t.MaskSha256 is not null);
        var splat = await db.WallGeometrySplats.SingleAsync();
        Assert.Equal(SHA256.HashData(s.SplatClient.Spz), splat.Sha256);
        foreach (var level in SplatLodLadder.Parse(splat.LodLevelsJson))
        {
            Assert.Equal(ContentHash.ToHex(await FileHashAsync(s, level.StoredPath)), level.Sha);
        }
    }

    [Fact]
    public async Task TheLadder_HashesEveryLevelItSaves()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var spz = SpzDecimatorTests.Spz(60_000, shDegree: 0);

        var (_, levels) = await SplatLodBackfill.SaveLadderAsync(spz, s.Files, default);

        Assert.NotEmpty(levels);
        foreach (var level in levels)
        {
            Assert.Equal(ContentHash.ToHex(await FileHashAsync(s, level.StoredPath)), level.Sha);
        }

        Assert.Equal(levels, SplatLodLadder.Parse(SplatLodLadder.Serialize(levels)));
    }

    [Fact]
    public void AnImportedTexture_TakesItsHashesFromTheCheckedFileList_NotFromTheRow()
    {
        var texture = new WallGeometryTexture
        {
            FacetId = "0", StoredPath = "a.jpg", MaskStoredPath = "m.png", Sha256 = new byte[32],
        };
        var image = SHA256.HashData([1]);
        var mask = SHA256.HashData([2]);

        CapturePackageRowCopies.StampHashes(
            [texture],
            [new CapturePackageFile("a.jpg", 1, ContentHash.ToHex(image)!, "texture"), new CapturePackageFile("m.png", 1, ContentHash.ToHex(mask)!, "texture-mask")]);

        Assert.Equal(image, texture.Sha256);
        Assert.Equal(mask, texture.MaskSha256);
    }

    private static Task<byte[]?> FileHashAsync(CaptureScenario s, string storedName) =>
        ContentHash.OfFileAsync(s.Files.ResolvePhysicalPath(storedName), default);
}
