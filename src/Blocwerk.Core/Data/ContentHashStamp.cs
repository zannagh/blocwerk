// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Assets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Blocwerk.Core.Data;

/// <summary>
/// Keeps the hash columns of <see cref="ContentHashColumn.InDatabase"/> in step with their blobs: on every save, an
/// Added row, or a Modified row whose blob or hash changed, gets the hash of its current bytes (null for none). A hash
/// set by hand is therefore overwritten; the column only ever holds the hash of the bytes beside it.
/// </summary>
/// <remarks>
/// Run by <see cref="BlocwerkDbContext"/> itself before <c>base.SaveChanges</c>, so every context has it whatever
/// factory built it, and it runs BEFORE every <c>SaveChangesInterceptor</c>, so an interceptor reading the entry (the
/// live feed) sees the hash with the bytes. (One added in <c>OnConfiguring</c> would run after the ones the factory
/// registers.) The change journal leaves hash columns out: a revert or replay writes the bytes, and this stamps them
/// again. Bulk writers
/// (<c>ExecuteUpdate</c>) bypass this and set the hash in the same <c>SetProperty</c> call as the bytes.
/// Hashing costs about 20 ms per 7 MB photo, on the writer's path only.
/// </remarks>
internal static class ContentHashStamp
{
    private static readonly ILookup<Type, ContentHashColumn> ByEntity = ContentHashColumn.InDatabase.ToLookup(c => c.Entity);

    public static void Apply(ChangeTracker tracker)
    {
        foreach (var entry in tracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            foreach (var column in ByEntity[entry.Metadata.ClrType])
            {
                Stamp(entry, column);
            }
        }
    }

    private static void Stamp(EntityEntry entry, ContentHashColumn column)
    {
        var bytes = entry.Property(column.Bytes);
        var hash = entry.Property(column.Hash);
        if (entry.State == EntityState.Modified && !bytes.IsModified && !hash.IsModified)
        {
            return;
        }

        var current = ContentHash.Of((byte[]?)bytes.CurrentValue);
        if (entry.State == EntityState.Added || !ContentHash.Same(current, (byte[]?)hash.CurrentValue))
        {
            hash.CurrentValue = current;
        }
    }
}
