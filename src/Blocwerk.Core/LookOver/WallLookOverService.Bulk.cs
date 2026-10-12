// <copyright file="WallLookOverService.Bulk.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.LookOver;

/// <summary>
/// The one bulk answer that removes holds: holds the update could not find and that no current boulder uses, all of
/// them or only those lying under a new hold. It is never blind: only the findings the user confirmed, and of those
/// only the ones that are still open, still unconfirmed and still unused when it runs. The holds go through the same
/// preparation as every hold deletion (<see cref="HoldDeletion"/>), all in one save and one journal batch, so the
/// whole step can be reverted as one.
/// </summary>
public sealed partial class WallLookOverService
{
    /// <summary>The journal batch label of the bulk removal.</summary>
    public const string RemoveBatchLabel = "look-over-remove-kept";

    /// <inheritdoc/>
    public async Task<LookOverBulkResult> RemoveUnusedAsync(Guid wallId, IReadOnlyCollection<Guid> findingIds, bool onlyCovered, CancellationToken ct = default)
    {
        var (db, access) = await OpenAsync(wallId, ct);
        await using (db)
        {
            if (!access.CanEdit)
            {
                throw new UnauthorizedAccessException($"User {access.UserId} may not remove holds on wall {wallId}.");
            }

            var asked = findingIds.Distinct().ToList();
            var open = await db.WallLookOverFindings
                .Where(f => f.WallId == wallId && f.Status == LookOverFindingStatus.Open && asked.Contains(f.Id) && (!onlyCovered || f.Covered))
                .ToListAsync(ct);
            var views = (await ToViewsAsync(db, wallId, open, access, ct)).ToDictionary(v => v.Id);
            using var batch = journal?.BeginBatch(RemoveBatchLabel, ChangeJournalScopeKind.Wall, wallId);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var holdIds = new List<Guid>();
            foreach (var finding in open)
            {
                // The view is what the hold is NOW: live, still not confirmed, on no boulder (and still under its new hold).
                var view = views[finding.Id];
                if (view is { HoldId: { } holdId, HoldKept: true, Boulders.Count: 0 } && (!onlyCovered || view.OtherHoldId is not null)
                    && await ClaimAsync(db, finding.Id, LookOverFindingStatus.Open, ct))
                {
                    holdIds.Add(holdId);
                    MarkSettled(finding, LookOverAnswer.RemoveHold, access.UserId, 0);
                }
            }

            await HoldDeletion.PrepareHoldsForDeleteAsync(db, holdIds, ct: ct);
            db.Holds.RemoveRange(await db.Holds.Where(h => holdIds.Contains(h.Id)).ToListAsync(ct));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            logger.LogInformation(
                "{Count} of {Asked} holds the update could not find removed at once from wall {WallId} by {UserId} (only under a new hold: {Covered})",
                holdIds.Count, asked.Count, wallId, access.UserId, onlyCovered);
            return new LookOverBulkResult(holdIds.Count, asked.Count);
        }
    }
}
