// <copyright file="LookOverCrops.Extent.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.LookOver;

/// <summary>
/// "Before" and "now" of one spot have to show the same piece of wall, or they cannot be compared: the two photos are
/// framed and sized differently, so the same normalised radius is not the same extent. The "before" picture is cut as
/// always (three radii of the old hold); the "now" picture is cut to that extent as it lies on the new photo, by the
/// local scale between the photos (<see cref="LookOverScale"/>, from the holds carried nearby). Without a reliable
/// scale the "now" picture falls back to its own radius, as before.
/// </summary>
internal static partial class LookOverCrops
{
    /// <summary>The radius the "before" picture is cut around, normalised to the old photo's long side.</summary>
    private static double BeforeRadius(WallLookOverFinding finding, Hold old) =>
        finding.Kind == LookOverFindingKind.SameSpot ? Math.Max(old.Radius, finding.Radius) : old.Radius;

    /// <summary>What the "now" picture has to show to match "before": half its side and the ring, in px of the new photo; null without a scale.</summary>
    private static async Task<(double HalfPx, double RingPx)?> SameExtentAsync(
        BlocwerkDbContext db, WallLookOverFinding finding, Guid newPanelId, (int Width, int Height) newSize, CancellationToken ct)
    {
        if (await HoldAsync(db, finding.OldHoldId, ct) is not { WallPanelId: { } oldPanelId } old)
        {
            return null;
        }

        var oldPhoto = await db.WallPanels.AsNoTracking().Where(p => p.Id == oldPanelId).Select(p => p.Photo).FirstOrDefaultAsync(ct);
        if (oldPhoto is null || OverlapSeedLoader.RawSize(oldPhoto) is not { } oldSize)
        {
            return null;
        }

        // The holds the update carried from the old photo onto the new one: known in both, so they measure the scale.
        var carried = await (
            from link in db.HoldGenerationLinks.AsNoTracking()
            where link.WallId == finding.WallId && link.ToGeneration == finding.Generation && link.OldHoldId != finding.OldHoldId
            join before in db.Holds.AsNoTracking() on link.OldHoldId equals before.Id
            join after in db.Holds.AsNoTracking() on link.NewHoldId equals after.Id
            where before.WallPanelId == oldPanelId && after.WallPanelId == newPanelId
            select new { OldX = before.X, OldY = before.Y, NewX = after.X, NewY = after.Y }).ToListAsync(ct);
        var scale = LookOverScale.Local(
            (old.X * oldSize.Width, old.Y * oldSize.Height),
            (finding.X * newSize.Width, finding.Y * newSize.Height),
            carried.Select(c => ((c.OldX * oldSize.Width, c.OldY * oldSize.Height), (c.NewX * newSize.Width, c.NewY * newSize.Height))));
        if (scale is not { } s)
        {
            return null;
        }

        var ringOld = BeforeRadius(finding, old) * Math.Max(oldSize.Width, oldSize.Height);
        return (RingCrop.HalfFor(ringOld) * s, ringOld * s);
    }
}
