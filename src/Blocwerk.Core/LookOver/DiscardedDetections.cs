// <copyright file="DiscardedDetections.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Linq.Expressions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.HoldStacks;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.LookOver;

/// <summary>
/// The detections a panel update's triage discarded, kept past the promote so they can still be added as holds. They
/// are look-over findings of their own kind (<see cref="LookOverFindingKind.DiscardedDetection"/>), never hold rows,
/// so no query over the wall's holds sees them. Each keeps what the detection was: panel, spot, radius, outline,
/// confidence and the triage's reason. A later update of the same panel removes them.
/// </summary>
internal static class DiscardedDetections
{
    /// <summary>
    /// The findings that count as open and flag the wall: every other kind, and of the discarded detections only those
    /// likely to be a missed hold (<see cref="LookOverRules.LikelyMissed"/>, on the overlap last worked out).
    /// </summary>
    public static readonly Expression<Func<WallLookOverFinding, bool>> Counts =
        f => f.Kind != LookOverFindingKind.DiscardedDetection
            || (f.Confidence >= LookOverRules.SureDetection && f.Overlap < LookOverRules.NoOverlapBelow);

    /// <summary>Whether a staged hold the promote deletes is a detection the triage discarded.</summary>
    /// <param name="hold">The deleted staged hold.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="generation">The generation the promote produces.</param>
    /// <returns>True when it is kept as a finding.</returns>
    public static bool IsOne(Hold hold, Guid wallId, int generation) =>
        hold.WallId == wallId && hold.Generation == generation && hold is { IsAutoDetected: true, IsVirtual: false, WallPanelId: not null, DiscardReason: not null };

    /// <summary>The finding that keeps a discarded detection.</summary>
    /// <param name="detection">The staged hold as it was detected.</param>
    /// <returns>An open finding (not added to any context).</returns>
    public static WallLookOverFinding From(Hold detection) => new()
    {
        WallId = detection.WallId,
        Generation = detection.Generation,
        Kind = LookOverFindingKind.DiscardedDetection,
        PanelId = detection.WallPanelId,
        X = detection.X,
        Y = detection.Y,
        Radius = detection.Radius,
        Confidence = detection.Confidence,
        DiscardReason = detection.DiscardReason,
        ShapePoints = detection.ShapePoints?.Select(p => new ShapePoint { Dx = p.Dx, Dy = p.Dy }).ToList(),
        ShapeHoles = ShapePoint.CloneRings(detection.ShapeHoles),
    };

    /// <summary>The detection as a hold that is in no context: for the code that draws or measures holds.</summary>
    /// <param name="finding">The finding.</param>
    /// <returns>The hold.</returns>
    public static Hold AsHold(WallLookOverFinding finding) => new()
    {
        WallId = finding.WallId,
        WallPanelId = finding.PanelId,
        Generation = finding.Generation,
        X = finding.X,
        Y = finding.Y,
        Radius = finding.Radius,
        ShapePoints = finding.ShapePoints,
        ShapeHoles = finding.ShapeHoles,
    };

    /// <summary>The area of a discarded detection on its photo.</summary>
    /// <param name="finding">The finding.</param>
    /// <param name="size">The photo's pixel size.</param>
    /// <returns>The area.</returns>
    public static HoldArea Area(WallLookOverFinding finding, (int Width, int Height)? size) =>
        new(finding.X, finding.Y, finding.Radius, finding.ShapePoints, size);

    /// <summary>
    /// How much of a detection live holds of its own kind cover, 0..1. An outlined hold that is clearly larger than the
    /// detection does not count: the detection would sit ON it (a hold on a volume), so it is no second detection of it.
    /// </summary>
    /// <param name="detection">The detection's area.</param>
    /// <param name="holds">The live holds' areas on the same photo.</param>
    /// <returns>The covered share.</returns>
    public static double Overlap(HoldArea detection, IEnumerable<HoldArea> holds) =>
        detection.CoveredBy(holds.Where(h => !HoldStacking.ClearlyLarger(h, detection)));

    /// <summary>
    /// The areas of the live holds on some panels, per panel, with each photo's size. Virtual holds and holds the
    /// update could not find and nobody confirmed are left out: neither says a hold is really on that spot.
    /// </summary>
    /// <param name="db">The context.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="panelIds">The panels.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Panel to its photo size and hold areas.</returns>
    public static async Task<Dictionary<Guid, ((int Width, int Height)? Size, List<HoldArea> Holds)>> LiveAreasAsync(
        BlocwerkDbContext db, Guid wallId, List<Guid> panelIds, CancellationToken ct)
    {
        var sizes = await LookOverFrameSource.SizesAsync(db, panelIds, ct);
        var live = await (await LiveWallHolds.QueryAsync(db, wallId, ct)).AsNoTracking()
            .Where(h => h.WallPanelId != null && panelIds.Contains(h.WallPanelId.Value) && !h.IsVirtual && h.KeptUnconfirmedGeneration == null)
            .Select(h => new { PanelId = h.WallPanelId!.Value, h.X, h.Y, h.Radius, h.ShapePoints })
            .ToListAsync(ct);
        var byPanel = live.ToLookup(h => h.PanelId);
        return panelIds.ToDictionary(
            id => id,
            id =>
            {
                (int Width, int Height)? size = sizes.TryGetValue(id, out var known) ? known : null;
                return (size, byPanel[id].Select(h => new HoldArea(h.X, h.Y, h.Radius, h.ShapePoints, size)).ToList());
            });
    }

    /// <summary>
    /// Works out again how much of each open discarded detection the live holds cover, and saves what changed: a hold
    /// added or removed since moves a detection between "likely missed" and "probably a duplicate".
    /// </summary>
    /// <param name="db">The context (saved here when anything changed).</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many findings changed.</returns>
    public static async Task<int> RefreshOverlapAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        var open = await db.WallLookOverFindings
            .Where(f => f.WallId == wallId && f.Kind == LookOverFindingKind.DiscardedDetection && f.Status == LookOverFindingStatus.Open && f.PanelId != null)
            .ToListAsync(ct);
        if (open.Count == 0)
        {
            return 0;
        }

        var areas = await LiveAreasAsync(db, wallId, open.Select(f => f.PanelId!.Value).Distinct().ToList(), ct);
        var changed = 0;
        foreach (var finding in open)
        {
            var (size, holds) = areas[finding.PanelId!.Value];
            var overlap = Math.Round(Overlap(Area(finding, size), holds), 3);
            if (finding.Overlap != overlap)
            {
                finding.Overlap = overlap;
                changed++;
            }
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return changed;
    }

    /// <summary>
    /// Flags the wall for a look-over when a discarded detection of <paramref name="generation"/> is likely a missed
    /// hold. The rest of them never flag the wall by themselves.
    /// </summary>
    /// <param name="db">The context (saved here).</param>
    /// <param name="wall">The tracked wall.</param>
    /// <param name="generation">The generation the detections belong to.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when the wall is flagged for them.</returns>
    public static async Task<bool> FlagIfLikelyMissedAsync(BlocwerkDbContext db, Wall wall, int generation, CancellationToken ct)
    {
        await RefreshOverlapAsync(db, wall.Id, ct);
        var likely = await db.WallLookOverFindings
            .Where(f => f.WallId == wall.Id && f.Generation == generation && f.Kind == LookOverFindingKind.DiscardedDetection && f.Status == LookOverFindingStatus.Open)
            .AnyAsync(Counts, ct);
        if (likely)
        {
            wall.LookOverPendingSince ??= DateTimeOffset.UtcNow;
            wall.LookOverGeneration = generation;
            await db.SaveChangesAsync(ct);
        }

        return likely;
    }
}
