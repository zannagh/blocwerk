// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Assets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Blocwerk.Core.Data;

/// <summary>
/// Keeps the hash columns of <see cref="ContentHashColumn.InDatabase"/> in step with their blobs: on every save, an
/// Added row, or a Modified row whose blob changed, gets the hash of its current bytes (null for none), or the hash a
/// writer carried with exactly those bytes (<see cref="ContentHashCarries"/>). A hash set by hand over unchanged bytes is
/// reverted to its stored value.
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

    /// <summary>
    /// Stamps every added or modified row of a stamped type. Detects changes on those rows only (auto-detect is off for
    /// the scan and restored after), so a save of thousands of holds is not scanned twice: the save's own detection runs
    /// once afterwards. A new row that only that detection finds (reachable through a navigation alone) is stamped by
    /// <see cref="StampDiscovered"/>.
    /// </summary>
    public static void Apply(ChangeTracker tracker, ContentHashCarries carries)
    {
        var auto = tracker.AutoDetectChangesEnabled;
        tracker.AutoDetectChangesEnabled = false;
        try
        {
            foreach (var entry in tracker.Entries().Where(e => ByEntity.Contains(e.Metadata.ClrType)).ToList())
            {
                entry.DetectChanges();
                StampEntry(entry, carries);
            }
        }
        finally
        {
            tracker.AutoDetectChangesEnabled = auto;
            carries.Clear();
        }
    }

    /// <summary>Stamps a row that started being tracked during a save (found through a navigation); none other.</summary>
    public static void StampDiscovered(EntityTrackedEventArgs e, ContentHashCarries carries)
    {
        if (!e.FromQuery && e.Entry.State == EntityState.Added && ByEntity.Contains(e.Entry.Metadata.ClrType))
        {
            StampEntry(e.Entry, carries);
        }
    }

    private static void StampEntry(EntityEntry entry, ContentHashCarries carries)
    {
        if (entry.State is not (EntityState.Added or EntityState.Modified))
        {
            return;
        }

        foreach (var column in ByEntity[entry.Metadata.ClrType])
        {
            Stamp(entry, column, carries);
        }
    }

    private static void Stamp(EntityEntry entry, ContentHashColumn column, ContentHashCarries carries)
    {
        var bytes = entry.Property(column.Bytes);
        var hash = entry.Property(column.Hash);
        if (entry.State == EntityState.Modified && !bytes.IsModified)
        {
            // A hash set by hand over unchanged bytes is put back, so the row is not written for it (and nothing journals
            // or feeds it). Never rehash here: a wall read without its photos holds null bytes beside a real hash.
            if (hash.IsModified)
            {
                hash.CurrentValue = hash.OriginalValue;
                hash.IsModified = false;
            }

            return;
        }

        var value = (byte[]?)bytes.CurrentValue;
        var current = carries.Find(entry.Entity, column.Hash, value) ?? ContentHash.Of(value);
        if (entry.State == EntityState.Added || !ContentHash.Same(current, (byte[]?)hash.CurrentValue))
        {
            hash.CurrentValue = current;
        }
    }
}
