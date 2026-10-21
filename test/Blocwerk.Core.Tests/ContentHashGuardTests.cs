// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Assets;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Every image the app serves has a content hash column: each <c>byte[]</c> in the model is a stamped blob
/// (<see cref="ContentHashColumn.InDatabase"/>), a hash itself, or deliberately unhashed below with a reason; and each
/// stored-file column of an asset row is hashed by its writer or deliberately unhashed. A new image column or stored-file
/// column fails here until it is placed. That saving bytes leaves the hash non-null is
/// <see cref="ContentHashStampTests.EveryStampedColumn_IsHashedOnInsert"/> and the writer tests.
/// </summary>
public class ContentHashGuardTests
{
    /// <summary>Blobs that are never served on the hashed asset path, with why.</summary>
    private static readonly Dictionary<(Type, string), string> UnhashedBlobs = new()
    {
        [(typeof(BetaVideo), nameof(BetaVideo.Data))] = "legacy in-database clip bytes; video bytes stay network-only",
        [(typeof(WallPanelCrop), nameof(WallPanelCrop.OriginalPhoto))] = "the photo a crop was made from; crops stay network-only",
        [(typeof(JournalBlob), nameof(JournalBlob.Bytes))] = "change-journal storage, keyed by its own Sha256, never served",
    };

    /// <summary>Stored-file columns of asset rows and the column holding each file's hash (the backfill's list too).</summary>
    private static readonly Dictionary<(Type, string), string> HashedFiles =
        ContentHashFileColumn.Stored.ToDictionary(c => (c.Entity, c.Path), c => c.Hash);

    /// <summary>Stored-file columns that are never served on the hashed asset path, with why.</summary>
    private static readonly Dictionary<(Type, string), string> UnhashedFiles = new()
    {
        [(typeof(BetaVideo), nameof(BetaVideo.StoragePath))] = "video bytes and HLS stay network-only",
        [(typeof(WallGeometrySplat), nameof(WallGeometrySplat.UncleanedStoredPath))] = "kept to revert the floater clean-up; not viewed",
        [(typeof(WallGeometryTexture), nameof(WallGeometryTexture.SourceMapStoredPath))] = "working data; hashed as a §6.5 document if at all",
        [(typeof(WallCapturePhoto), nameof(WallCapturePhoto.StoredPath))] = "capture input (close-ups stay network-only)",
        [(typeof(WallCapture), nameof(WallCapture.VideoStoredPath))] = "capture input",
        [(typeof(WallCapture), nameof(WallCapture.SparsePointsStoredPath))] = "capture working data",
        [(typeof(CaptureDetectionRecord), nameof(CaptureDetectionRecord.StoredPath))] = "detector record, not an asset",
    };

    [Fact]
    public void EveryBlob_IsStamped_OrAHash_OrDeliberatelyUnhashed()
    {
        using var h = new WallTestHarness();
        using var db = h.CreateContext();
        var stamped = ContentHashColumn.InDatabase.ToDictionary(c => (c.Entity, c.Bytes), c => c.Hash);
        var hashes = ContentHashColumn.InDatabase.Select(c => (c.Entity, c.Hash))
            .Concat(HashedFiles.Select(f => (f.Key.Item1, f.Value)))
            .ToHashSet();

        var unplaced = db.Model.GetEntityTypes()
            .SelectMany(e => e.GetProperties().Where(p => p.ClrType == typeof(byte[])).Select(p => (e.ClrType, p.Name)))
            .Where(p => !stamped.ContainsKey(p) && !hashes.Contains(p) && !UnhashedBlobs.ContainsKey(p))
            .Select(p => $"{p.ClrType.Name}.{p.Name}")
            .ToList();

        Assert.True(unplaced.Count == 0, $"Give these a hash column in ContentHashColumn.InDatabase, or list them as unhashed: {string.Join(", ", unplaced)}");
        foreach (var (entity, hash) in hashes)
        {
            Assert.Equal(typeof(byte[]), db.Model.FindEntityType(entity)?.FindProperty(hash)?.ClrType);
        }
    }

    [Fact]
    public void EveryStoredFileColumn_IsHashed_OrDeliberatelyUnhashed()
    {
        using var h = new WallTestHarness();
        using var db = h.CreateContext();
        var unplaced = db.Model.GetEntityTypes()
            .SelectMany(e => e.GetProperties()
                .Where(p => p.ClrType == typeof(string) && (p.Name.EndsWith("StoredPath", StringComparison.Ordinal) || p.Name == "StoragePath"))
                .Select(p => (e.ClrType, p.Name)))
            .Where(p => !HashedFiles.ContainsKey(p) && !UnhashedFiles.ContainsKey(p))
            .Select(p => $"{p.ClrType.Name}.{p.Name}")
            .ToList();

        Assert.True(unplaced.Count == 0, $"Hash these files in their writer (and list them here), or list them as unhashed: {string.Join(", ", unplaced)}");
    }
}
