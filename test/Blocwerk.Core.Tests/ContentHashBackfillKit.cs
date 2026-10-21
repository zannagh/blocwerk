// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Assets;
using Blocwerk.Core.Assets.Backfill;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// One wall with rows of every hashed column, hashes nulled the way rows from before PR 2.1 hold them, and the backfill
/// over the same database (SQLite or Postgres) with its waits shortened, optionally with interceptors wired.
/// </summary>
internal sealed class ContentHashBackfillKit : IDisposable
{
    public static readonly ContentHashBackfillOptions FastOptions = new()
    {
        StartDelay = TimeSpan.Zero, RetryDelay = TimeSpan.Zero, MinPause = TimeSpan.Zero, InitialBlobBatch = 2, MaxBlobBatch = 4, FileBatch = 2,
    };

    private readonly string captureRoot = Path.Combine(Path.GetTempPath(), "blocwerk-hash-backfill", Guid.NewGuid().ToString("N"));

    public ContentHashBackfillKit(PostgresTestDatabase? postgres)
    {
        H = new WallTestHarness(postgres);
        var settings = new BlocwerkSettings();
        settings.WallImage.StoragePath = captureRoot;
        Captures = new FileSystemCaptureFileStore(settings);
        Images = new HookedWallImageStorage(H.WallImageStorage);
    }

    public WallTestHarness H { get; }

    public ICaptureFileStore Captures { get; }

    public HookedWallImageStorage Images { get; }

    public ContentHashBackfill Backfill(params IInterceptor[] interceptors) =>
        new(new InterceptingRootDbContextFactory(H.DbContextFactory.ConnectionString, interceptors), Images, Captures, FastOptions,
            NullLogger<ContentHashBackfill>.Instance);

    /// <summary>A stored gallery image with <paramref name="bytes"/>; returns its row id.</summary>
    public async Task<Guid> AddImageAsync(byte[] bytes)
    {
        var temp = H.WallImageStorage.CreateTempPath(".jpg");
        await File.WriteAllBytesAsync(temp, bytes);
        var stored = H.WallImageStorage.Commit(temp, ".jpg");
        await using var db = H.CreateContext();
        var image = new WallImage { WallId = H.WallId, StoragePath = stored, ContentType = "image/jpeg", SizeBytes = bytes.Length };
        db.WallImages.Add(image);
        await db.SaveChangesAsync();
        return image.Id;
    }

    /// <summary>A geometry model with a splat (full, mobile, a two-level ladder without hashes) and a masked texture.</summary>
    public async Task<(Guid Splat, Guid Texture)> AddGeometryAsync(string ladderJson)
    {
        await using var db = H.CreateContext();
        var model = new WallGeometryModel { WallId = H.WallId, Json = "{}", SchemaVersion = 1, Source = "test", IsActive = true };
        var splat = new WallGeometrySplat
        {
            GeometryModelId = model.Id, StoredPath = await Captures.SaveAsync([1, 1, 1], ".spz", default),
            MobileStoredPath = await Captures.SaveAsync([2, 2], ".spz", default), LodLevelsJson = ladderJson, FrameJson = "{}",
        };
        var texture = new WallGeometryTexture
        {
            GeometryModelId = model.Id, FacetId = "f0", StoredPath = await Captures.SaveAsync([3, 3, 3], ".jpg", default),
            MaskStoredPath = await Captures.SaveAsync([4], ".png", default),
        };
        db.AddRange(model, splat, texture);
        await db.SaveChangesAsync();
        return (splat.Id, texture.Id);
    }

    /// <summary>Clears every hash column, blobs and files, as rows stored before the hashes were stamped hold them.</summary>
    public async Task NullEveryHashAsync()
    {
        await using var db = H.CreateContext();
        await db.WallPanels.ExecuteUpdateAsync(s => s.SetProperty(p => p.PhotoSha256, (byte[]?)null).SetProperty(p => p.StagedPhotoSha256, (byte[]?)null));
        await db.Walls.IgnoreQueryFilters().ExecuteUpdateAsync(s => s.SetProperty(w => w.PhotoSha256, (byte[]?)null).SetProperty(w => w.StagedPhotoSha256, (byte[]?)null));
        await db.WallResets.IgnoreQueryFilters().ExecuteUpdateAsync(s => s.SetProperty(r => r.PreviousPhotoSha256, (byte[]?)null));
        await db.Users.IgnoreQueryFilters().ExecuteUpdateAsync(s => s.SetProperty(u => u.AvatarSha256, (byte[]?)null));
        await db.BetaVideos.IgnoreQueryFilters().ExecuteUpdateAsync(s => s.SetProperty(v => v.ThumbnailSha256, (byte[]?)null));
        await db.WallImages.IgnoreQueryFilters().ExecuteUpdateAsync(s => s.SetProperty(i => i.Sha256, (byte[]?)null));
        await db.WallGeometrySplats.ExecuteUpdateAsync(s => s.SetProperty(x => x.Sha256, (byte[]?)null).SetProperty(x => x.MobileSha256, (byte[]?)null));
        await db.WallGeometryTextures.ExecuteUpdateAsync(s => s.SetProperty(x => x.Sha256, (byte[]?)null).SetProperty(x => x.MaskSha256, (byte[]?)null));
    }

    /// <summary>Every blob column's stored hash next to the hash of its stored bytes, over all rows.</summary>
    public async Task<List<(string Column, byte[]? Expected, byte[]? Stored)>> BlobHashesAsync()
    {
        await using var db = H.CreateContext();
        var pairs = new List<(string, byte[]?, byte[]?)>();
        foreach (var column in ContentHashColumn.InDatabase)
        {
            var type = db.Model.FindEntityType(column.Entity)!;
            foreach (var entity in await QueryAll(db, column.Entity))
            {
                var entry = db.Entry(entity);
                pairs.Add(($"{type.ClrType.Name}.{column.Bytes}", ContentHash.Of((byte[]?)entry.Property(column.Bytes).CurrentValue),
                    (byte[]?)entry.Property(column.Hash).CurrentValue));
            }
        }

        return pairs;
    }

    public void Dispose()
    {
        H.Dispose();
        if (Directory.Exists(captureRoot))
        {
            Directory.Delete(captureRoot, recursive: true);
        }
    }

    private static async Task<List<object>> QueryAll(BlocwerkDbContext db, Type entity) => entity.Name switch
    {
        nameof(WallPanel) => [.. await db.WallPanels.IgnoreQueryFilters().AsNoTracking().ToListAsync()],
        nameof(Wall) => [.. await db.Walls.IgnoreQueryFilters().AsNoTracking().ToListAsync()],
        nameof(WallReset) => [.. await db.WallResets.IgnoreQueryFilters().AsNoTracking().ToListAsync()],
        nameof(User) => [.. await db.Users.IgnoreQueryFilters().AsNoTracking().ToListAsync()],
        nameof(BetaVideo) => [.. await db.BetaVideos.IgnoreQueryFilters().AsNoTracking().ToListAsync()],
        _ => throw new InvalidOperationException($"Add {entity.Name} to the kit."),
    };
}
