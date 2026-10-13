// <copyright file="WallLookOverService.Split.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.LookOver;

/// <summary>
/// "Different hold" on an unsure match. The update carried the old hold onto a detection, so the one live row is the
/// new hold on the wall wearing the old hold's name, colour, boulders and links. Saying it is a different hold takes
/// the two apart again: the row keeps the old hold's identity (nothing that points at it has to move) and becomes a
/// hold that is kept, not confirmed, and the detection goes on as a new row with the detected outline and nothing of
/// the old hold. From then on "not there any more" removes the old hold only.
/// </summary>
public sealed partial class WallLookOverService
{
    /// <summary>
    /// A finding somebody called a different hold before the pair was taken apart on that answer: its hold still is
    /// the detection wearing the old identity, and must be split before anything is removed.
    /// </summary>
    private static bool IsUnsplit(WallLookOverFinding finding) => finding is { Converted: true, HoldKept: false };

    /// <summary>
    /// Takes the pair apart on the caller's context (the caller saves): adds the detection as a new hold and turns
    /// the existing row back into the old hold, kept and not confirmed, with the old outline where the previous
    /// generation still has it. The spot stays the one the update matched the old hold to on the new photo.
    /// </summary>
    private static async Task<Hold> SplitAsync(BlocwerkDbContext db, WallLookOverFinding finding, Guid holdId, CancellationToken ct)
    {
        var hold = await db.Holds.FirstAsync(h => h.Id == holdId, ct);
        var detection = hold.Clone();
        detection.Id = Guid.NewGuid();
        (detection.Name, detection.Color, detection.ProposedColor, detection.Material, detection.HandType) = (null, null, null, null, null);
        (detection.Category, detection.NeedsReview, detection.KeptUnconfirmedGeneration) = (HoldCategory.Hand, false, null);
        db.Holds.Add(detection);

        var old = finding.OldHoldId is { } oldId && oldId != holdId
            ? await db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.Id == oldId && h.WallId == finding.WallId, ct)
            : null;
        if (old is not null)
        {
            var outline = old.Clone();
            (hold.Radius, hold.ShapePoints, hold.ShapeHoles) = (outline.Radius, outline.ShapePoints, outline.ShapeHoles);
            hold.CopyGlyphMetricsFrom(old);
        }

        hold.KeptUnconfirmedGeneration = finding.Generation;
        (finding.HoldKept, finding.OtherHoldId, finding.Covered) = (true, detection.Id, SameSpotFinder.Covers(detection, hold, null));
        return detection;
    }

    /// <summary>
    /// Suggests a colour for the new hold from its photo, when that can be read. Never part of the answer: a hold
    /// without a suggestion simply has no colour, as any new hold the proposer cannot read.
    /// </summary>
    private async Task SuggestColorAsync(BlocwerkDbContext db, Guid wallId, IReadOnlyCollection<Guid> holdIds, CancellationToken ct)
    {
        if (colors is null || holdIds.Count == 0)
        {
            return;
        }

        try
        {
            var plan = await colors.PlanAsync(db, wallId, holdIds, ct);
            await colors.ApplyAsync(db, wallId, plan, ct);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "No colour suggestion for the new hold {HoldId} on wall {WallId}", holdIds.First(), wallId);
        }
    }
}
