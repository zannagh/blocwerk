// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.Corrections;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Capture.Retention;

/// <summary>
/// Which models <see cref="SupersededModelRetention"/> strips. Per wall without work in flight (<see cref="RetentionBusyWalls"/>),
/// the families (<see cref="ModelFamily"/>) are sorted into:
/// <list type="bullet">
/// <item>protected: the active model's family, and the family of every model a protected or kept model carries
/// textures from (<see cref="RegisteredGeometry.Carried"/>: a re-render copies them again);</item>
/// <item>revert candidates: families that were active once (<see cref="WallGeometryModel.RetiredAt"/>) and still have
/// files, newest retirement first; the first <c>keep</c> keep their files, the rest lose them once retired longer than
/// the grace;</item>
/// <item>never active (an upload stored inactive, a capture awaiting an admin's decision): kept as long as the capture
/// photos (at least the grace), then stripped; they never take a revert slot.</item>
/// </list>
/// </summary>
internal static class SupersededModelSelection
{
    /// <summary>The models whose files go (of <paramref name="wallId"/> only, when given).</summary>
    public static async Task<List<Guid>> SelectAsync(
        BlocwerkDbContext db, int keep, WallCapturePipelineOptions options, DateTimeOffset now, CancellationToken ct, Guid? wallId = null)
    {
        var models = await db.WallGeometryModels.AsNoTracking()
            .Where(m => wallId == null || m.WallId == wallId)
            .Select(m => new RetainedModel(m.Id, m.WallId, m.IsActive, m.DerivedFromModelId, m.RetiredAt, m.CreatedAt))
            .ToListAsync(ct);
        var withFiles = (await db.WallGeometryTextures.Select(t => t.GeometryModelId).Distinct().ToListAsync(ct))
            .Concat(await db.WallGeometrySplats.Select(s => s.GeometryModelId).ToListAsync(ct))
            .ToHashSet();
        var pending = (await db.WallCaptures
                .Where(c => c.Status == WallCaptureStatus.StoredNotActivated && c.GeometryModelId != null)
                .Select(c => c.GeometryModelId!.Value)
                .ToListAsync(ct))
            .ToHashSet();
        var busy = await RetentionBusyWalls.ListAsync(db, ct);
        var doomed = new List<Guid>();
        foreach (var wall in models.GroupBy(m => m.WallId).Where(w => !busy.Contains(w.Key)))
        {
            if (wall.FirstOrDefault(m => m.IsActive) is { } active)
            {
                var families = Families(wall.ToList(), withFiles, pending);
                doomed.AddRange(await SelectOnWallAsync(db, families, active, keep, options, now, ct));
            }
        }

        return doomed;
    }

    private static async Task<IEnumerable<Guid>> SelectOnWallAsync(
        BlocwerkDbContext db, List<RetainedFamily> families, RetainedModel active, int keep, WallCapturePipelineOptions options,
        DateTimeOffset now, CancellationToken ct)
    {
        var rootOf = families.SelectMany(f => f.Members.Select(m => (m.Id, f.Root))).ToDictionary(x => x.Id, x => x.Root);
        var protectedRoots = new HashSet<Guid> { rootOf[active.Id] };
        var activeFamily = families.Single(f => f.Root == rootOf[active.Id]);
        await ProtectReferencesAsync(db, activeFamily.Members, rootOf, protectedRoots, ct);

        var candidates = families.Where(f => f.HasFiles && !protectedRoots.Contains(f.Root)).ToList();
        var ranked = candidates.Where(f => f.EverActive && !f.Pending)
            .OrderByDescending(f => f.Since).ThenByDescending(f => f.CreatedAt)
            .ToList();
        var kept = ranked.Take(keep).ToList();
        await ProtectReferencesAsync(db, kept.SelectMany(f => f.Members), rootOf, protectedRoots, ct);

        var pendingGrace = options.PhotoRetention is { } photos ? Max(photos, options.SupersededModelGrace) : (TimeSpan?)null;
        var superseded = ranked.Skip(keep).Where(f => now - f.Since >= options.SupersededModelGrace);
        var undecided = candidates.Where(f => (!f.EverActive || f.Pending) && pendingGrace is { } grace && now - f.Since >= grace);
        return superseded.Concat(undecided)
            .Where(f => !protectedRoots.Contains(f.Root))
            .SelectMany(f => f.Members)
            .Where(m => m.HasFiles)
            .Select(m => m.Id);
    }

    private static List<RetainedFamily> Families(List<RetainedModel> wall, HashSet<Guid> withFiles, HashSet<Guid> pending)
    {
        var parents = wall.ToDictionary(m => m.Id, m => m.DerivedFromModelId);
        return wall.GroupBy(m => ModelFamily.Root(parents, m.Id))
            .Select(f => new RetainedFamily(
                f.Key,
                f.Select(m => m with { HasFiles = withFiles.Contains(m.Id) }).ToList(),
                Pending: f.Any(m => pending.Contains(m.Id))))
            .ToList();
    }

    /// <summary>Adds the families of the models <paramref name="members"/> carry textures from to <paramref name="protectedRoots"/>.</summary>
    private static async Task ProtectReferencesAsync(
        BlocwerkDbContext db, IEnumerable<RetainedModel> members, Dictionary<Guid, Guid> rootOf, HashSet<Guid> protectedRoots, CancellationToken ct)
    {
        var ids = members.Select(m => m.Id).ToList();
        var documents = await db.WallGeometryModels.AsNoTracking().Where(m => ids.Contains(m.Id)).Select(m => m.Json).ToListAsync(ct);
        foreach (var (reference, facets) in documents.Select(RegisteredGeometry.Carried))
        {
            if (reference is { } id && facets.Count > 0 && rootOf.TryGetValue(id, out var root))
            {
                protectedRoots.Add(root);
            }
        }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
