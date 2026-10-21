// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Blocwerk.Core.Data;

/// <summary>Stamps the blobs' content hashes before every save (see <see cref="ContentHashStamp"/>).</summary>
public partial class BlocwerkDbContext
{
    private readonly ContentHashCarries contentHashCarries = new();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ChangeTracker.Tracked += StampDiscovered;
        try
        {
            ContentHashStamp.Apply(ChangeTracker, contentHashCarries);
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }
        finally
        {
            ChangeTracker.Tracked -= StampDiscovered;
        }
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ChangeTracker.Tracked += StampDiscovered;
        try
        {
            ContentHashStamp.Apply(ChangeTracker, contentHashCarries);
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
        finally
        {
            ChangeTracker.Tracked -= StampDiscovered;
        }
    }

    /// <summary>The panel's staged photo just went live (<c>Photo = StagedPhoto</c>): its hash goes with it.</summary>
    internal void CarryStagedPhotoHash(Entities.WallPanel panel) => CarryStagedHash(
        panel, nameof(Entities.WallPanel.StagedPhoto), nameof(Entities.WallPanel.StagedPhotoSha256), nameof(Entities.WallPanel.PhotoSha256), panel.Photo);

    /// <summary>
    /// Moves a staged photo's stored hash along with its bytes: the live column (<paramref name="liveHash"/>) has just
    /// been given <paramref name="bytes"/>, the same array as the staged column, and the next save takes the staged hash
    /// instead of hashing them again. Only when the staged bytes and hash are as loaded (unchanged in this context) and
    /// the hash exists; otherwise the save hashes the bytes as usual.
    /// </summary>
    internal void CarryStagedHash(object entity, string stagedBytes, string stagedHash, string liveHash, byte[]? bytes)
    {
        var entry = Entry(entity);
        var staged = entry.Property(stagedBytes);
        var hash = entry.Property(stagedHash);
        if (bytes is not null && ReferenceEquals(staged.CurrentValue, bytes) && !staged.IsModified && !hash.IsModified
            && hash.CurrentValue is byte[] { Length: Assets.ContentHash.Length } sha)
        {
            contentHashCarries.Add(entity, liveHash, bytes, sha);
        }
    }

    /// <summary>
    /// Copies another row's live photo hash with its bytes (the wall photo taking the centre panel's): the hash
    /// <paramref name="source"/> holds or carries for <paramref name="bytes"/>, if any.
    /// </summary>
    internal void CarryHashFrom(object source, string sourceBytes, string sourceHash, object target, string targetHash, byte[]? bytes)
    {
        var entry = Entry(source);
        var sha = contentHashCarries.Find(source, sourceHash, bytes);
        if (sha is null && bytes is not null && ReferenceEquals(entry.Property(sourceBytes).CurrentValue, bytes)
            && !entry.Property(sourceBytes).IsModified && !entry.Property(sourceHash).IsModified)
        {
            sha = entry.Property(sourceHash).CurrentValue as byte[];
        }

        if (sha is { Length: Assets.ContentHash.Length })
        {
            contentHashCarries.Add(target, targetHash, bytes!, sha);
        }
    }

    private void StampDiscovered(object? sender, EntityTrackedEventArgs e) =>
        ContentHashStamp.StampDiscovered(e, contentHashCarries);
}
