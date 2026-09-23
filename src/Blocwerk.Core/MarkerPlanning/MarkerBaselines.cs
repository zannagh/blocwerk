// <copyright file="MarkerBaselines.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Data;
using Blocwerk.Core.Geometry;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// The marker layout of each of a wall's plan revisions, for comparing them (<see cref="MarkerPlanDiff"/>).
/// Revision null means "before any plan": the legacy <c>segment*6+role</c> markers, whose sizes and places
/// only a solved model knows — so that baseline is the wall's newest legacy model, read the same way
/// "Start from measured wall" reads it (<see cref="MarkerPlanFromGeometry"/>), which makes a revision-1
/// plan built from it compare as unchanged. Query filters are bypassed: callers have authorised the wall.
/// </summary>
public sealed class MarkerBaselines(BlocwerkDbContext db, Guid wallId)
{
    private static readonly PhotoSetup AnyPhoto = new(3000, "phone-1x", 69, 4032);
    private readonly Dictionary<int, IReadOnlyList<PlanMarker>?> revisions = [];
    private readonly Dictionary<(int?, int?), IReadOnlySet<int>?> stable = [];
    private IReadOnlyList<PlanMarker>? legacy;
    private bool legacyLoaded;

    /// <summary>The markers of <paramref name="revision"/> (null = legacy); null when unknown.</summary>
    public async Task<IReadOnlyList<PlanMarker>?> MarkersAsync(int? revision, CancellationToken ct = default)
    {
        if (revision is not { } r)
        {
            return await LegacyAsync(ct);
        }

        if (!revisions.TryGetValue(r, out var markers))
        {
            var json = await db.WallMarkerPlans.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.WallId == wallId && p.Revision == r)
                .OrderByDescending(p => p.CreatedAt)
                .Select(p => p.Json)
                .FirstOrDefaultAsync(ct);
            markers = json is null ? null : MarkerPlanJson.FromJson(json, out _)?.Markers;
            revisions[r] = markers;
        }

        return markers;
    }

    /// <summary>
    /// The ids that mean the same physical marker in both revisions; null when the two are the same
    /// revision (no restriction), empty when either layout is unknown (trust nothing).
    /// </summary>
    public async Task<IReadOnlySet<int>?> StableIdsAsync(int? from, int? to, CancellationToken ct = default)
    {
        if (from == to)
        {
            return null;
        }

        if (!stable.TryGetValue((from, to), out var ids))
        {
            var before = await MarkersAsync(from, ct);
            var after = await MarkersAsync(to, ct);
            ids = before is null || after is null ? new HashSet<int>() : MarkerPlanDiff.UnchangedIds(before, after);
            stable[(from, to)] = ids;
        }

        return ids;
    }

    /// <summary>The ids unchanged across ALL of <paramref name="revisionsToCompare"/>; null when they are all the same.</summary>
    public async Task<IReadOnlySet<int>?> StableIdsAsync(IEnumerable<int?> revisionsToCompare, CancellationToken ct = default)
    {
        var distinct = revisionsToCompare.Distinct().ToList();
        HashSet<int>? result = null;
        for (var i = 1; i < distinct.Count; i++)
        {
            var ids = await StableIdsAsync(distinct[0], distinct[i], ct) ?? throw new InvalidOperationException("Distinct revisions compared equal.");
            result = result is null ? ids.ToHashSet() : result.Intersect(ids).ToHashSet();
        }

        return result;
    }

    /// <summary>The wall's current plan revision, or null when it has no plan.</summary>
    public static Task<int?> CurrentRevisionAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct = default) =>
        db.WallMarkerPlans.IgnoreQueryFilters()
            .Where(p => p.WallId == wallId && p.IsCurrent)
            .Select(p => (int?)p.Revision)
            .FirstOrDefaultAsync(ct);

    /// <summary>A legacy-scheme model's markers as a plan reads them; null for a plan-scheme or unreadable model.</summary>
    internal static IReadOnlyList<PlanMarker>? TryLegacyMarkers(string json)
    {
        try
        {
            var document = WallGeometryDocument.Parse(json);
            return document.IdScheme == WallMarkerLayout.PlanIdScheme ? null : MarkerPlanFromGeometry.Build(document, AnyPhoto).Markers;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<PlanMarker>?> LegacyAsync(CancellationToken ct)
    {
        if (legacyLoaded)
        {
            return legacy;
        }

        legacyLoaded = true;
        var models = await db.WallGeometryModels.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.WallId == wallId && m.PlanRevision == null)
            .OrderByDescending(m => m.IsActive)
            .ThenByDescending(m => m.CreatedAt)
            .Select(m => m.Json)
            .Take(8)
            .ToListAsync(ct);
        foreach (var json in models)
        {
            if (TryLegacyMarkers(json) is { } markers)
            {
                legacy = markers;
                break;
            }
        }

        return legacy;
    }
}
