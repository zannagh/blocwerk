// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Data;

/// <summary>
/// A family always has a parent on the wall. Boulders are retired from many places (archive, delete, a wall update, a
/// moved or removed hold), so this runs on every save: when a parent is deleted, archived or goes historic, its oldest
/// published variant still on the wall becomes the parent and the others move under it. A retired parent that still
/// exists stays in the family as a variant. With no such variant the family waits, and the first variant back on the
/// wall takes over.
/// </summary>
internal static class BoulderVariantPromotion
{
    /// <summary>Promotes before a synchronous save.</summary>
    public static void Apply(BlocwerkDbContext db)
    {
        var (retired, waiting) = Triggers(db);
        if (retired.Count + waiting.Count == 0)
        {
            return;
        }

        var parents = db.Boulders.Where(b => waiting.Contains(b.Id)).ToList();
        var ids = Retired(retired, parents);
        if (ids.Count > 0)
        {
            db.Boulders.Where(b => b.ParentBoulderId != null && ids.Contains(b.ParentBoulderId.Value)).Load();
            Promote(db, ids);
        }
    }

    /// <summary>Promotes before an asynchronous save.</summary>
    public static async Task ApplyAsync(BlocwerkDbContext db, CancellationToken ct)
    {
        var (retired, waiting) = Triggers(db);
        if (retired.Count + waiting.Count == 0)
        {
            return;
        }

        var parents = await db.Boulders.Where(b => waiting.Contains(b.Id)).ToListAsync(ct);
        var ids = Retired(retired, parents);
        if (ids.Count > 0)
        {
            await db.Boulders.Where(b => b.ParentBoulderId != null && ids.Contains(b.ParentBoulderId.Value)).LoadAsync(ct);
            Promote(db, ids);
        }
    }

    /// <summary>The parents retired by this save, and the parents of variants that have just come onto the wall.</summary>
    private static (List<Boulder> Retired, List<Guid> Waiting) Triggers(BlocwerkDbContext db)
    {
        var retired = new List<Boulder>();
        var waiting = new List<Guid>();
        foreach (var entry in db.ChangeTracker.Entries<Boulder>())
        {
            var boulder = entry.Entity;
            var wasLive = entry.State != EntityState.Added
                && !entry.Property(b => b.IsDraft).OriginalValue
                && !entry.Property(b => b.IsArchived).OriginalValue
                && !entry.Property(b => b.IsHistoric).OriginalValue;
            if (boulder.ParentBoulderId is null
                && (entry.State == EntityState.Deleted || (entry.State == EntityState.Modified && wasLive && !IsLive(boulder))))
            {
                retired.Add(boulder);
            }
            else if (boulder.ParentBoulderId is { } parentId && entry.State is EntityState.Added or EntityState.Modified && !wasLive && IsLive(boulder))
            {
                waiting.Add(parentId);
            }
        }

        return (retired, waiting);
    }

    private static List<Guid> Retired(List<Boulder> retired, List<Boulder> parents) =>
        retired.Concat(parents.Where(p => !IsLive(p))).Select(b => b.Id).Distinct().ToList();

    // Reads the families from the change tracker: the queries above loaded them, and a variant being added in this very
    // save is only there.
    private static void Promote(BlocwerkDbContext db, List<Guid> parentIds)
    {
        foreach (var parentId in parentIds)
        {
            var parent = db.ChangeTracker.Entries<Boulder>().FirstOrDefault(e => e.Entity.Id == parentId);
            var deleted = parent?.State == EntityState.Deleted;
            var family = db.ChangeTracker.Entries<Boulder>()
                .Where(e => e.State != EntityState.Deleted && e.Entity.ParentBoulderId == parentId)
                .Select(e => e.Entity)
                .OrderBy(v => v.PublishedAt ?? v.CreatedAt)
                .ThenBy(v => v.Id)
                .ToList();
            var heir = family.FirstOrDefault(IsLive) ?? (deleted ? family.FirstOrDefault(v => v is { IsDraft: false, IsArchived: false }) : null);
            if (heir is null)
            {
                family.ForEach(v => v.ParentBoulderId = deleted ? null : v.ParentBoulderId);
                continue;
            }

            family.ForEach(v => v.ParentBoulderId = heir.Id);
            heir.ParentBoulderId = null;
            if (parent is { Entity: var retired } && !deleted)
            {
                retired.ParentBoulderId = heir.Id;
            }
        }
    }

    private static bool IsLive(Boulder b) => b is { IsDraft: false, IsArchived: false, IsHistoric: false };
}
