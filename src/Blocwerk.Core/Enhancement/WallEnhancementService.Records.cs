// <copyright file="WallEnhancementService.Records.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.HoldLinks;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Enhancement;

/// <summary>
/// The suggestion records an answer changes besides wall content: a hold proposal's status, a link suggestion, a duplicate
/// pair's dismissal. They are not journalled, so the answer stores what they were (<see cref="WallEnhancementItem.PriorState"/>)
/// and discarding the run puts them back: an added hold's proposal is pending again, a taken-back link's suggestion is
/// offered again, a "not a duplicate" is forgotten.
/// </summary>
public sealed partial class WallEnhancementService
{
    private const string NoRecord = "none";
    private const string PendingLinkPrefix = "pending:";

    /// <summary>The record's state before the answer, for <see cref="WallEnhancementItem.PriorState"/>.</summary>
    private async Task<string?> PriorStateAsync(Guid wallId, WallEnhancementItem item, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var (left, right) = (item.HoldId.GetValueOrDefault(), item.OtherHoldId.GetValueOrDefault());
        switch (item.Kind)
        {
            case WallEnhancementItemKind.NewHold:
                var status = await db.HoldProposals.AsNoTracking()
                    .Where(p => p.Id == item.ReferenceId && p.WallId == wallId).Select(p => (HoldProposalStatus?)p.Status).FirstOrDefaultAsync(ct);
                return status?.ToString();
            case WallEnhancementItemKind.Link:
                var (a, b) = HoldLinkPairSuggestion.Key(left, right);
                var row = await db.HoldLinkSuggestions.AsNoTracking()
                    .Where(s => s.WallId == wallId && s.HoldAId == a && s.HoldBId == b && s.Status == HoldLinkSuggestionStatus.Pending)
                    .Select(s => (double?)s.DistanceMm).FirstOrDefaultAsync(ct);
                return row is { } mm ? PendingLinkPrefix + mm.ToString("R", CultureInfo.InvariantCulture) : NoRecord;
            case WallEnhancementItemKind.Duplicate:
                var (da, dbId) = HoldMergeRules.PairKey(left, right);
                return await db.HoldDuplicateDismissals.AnyAsync(d => d.WallId == wallId && d.HoldAId == da && d.HoldBId == dbId, ct) ? "dismissed" : NoRecord;
            default:
                return null;
        }
    }

    /// <summary>Puts every answered suggestion's record back as it was before the answer. Returns how many were restored.</summary>
    private async Task<int> RestoreRecordsAsync(Guid wallId, IReadOnlyList<WallEnhancementItem> items, CancellationToken ct)
    {
        var answered = items.Where(i => !WallEnhancementKinds.IsWritten(i.Kind)
                                        && i.Decision is WallEnhancementDecision.Accepted or WallEnhancementDecision.Rejected
                                        && i.PriorState is not null).ToList();
        if (answered.Count == 0)
        {
            return 0;
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        foreach (var item in answered)
        {
            await RestoreAsync(db, wallId, item, ct);
        }

        await db.SaveChangesAsync(ct);
        return answered.Count;
    }

    private static async Task RestoreAsync(Data.BlocwerkDbContext db, Guid wallId, WallEnhancementItem item, CancellationToken ct)
    {
        var (left, right) = (item.HoldId.GetValueOrDefault(), item.OtherHoldId.GetValueOrDefault());
        switch (item.Kind)
        {
            case WallEnhancementItemKind.NewHold when Enum.TryParse<HoldProposalStatus>(item.PriorState, out var prior):
                if (await db.HoldProposals.FirstOrDefaultAsync(p => p.Id == item.ReferenceId && p.WallId == wallId, ct) is { } proposal)
                {
                    proposal.Status = prior;
                    (proposal.HoldId, proposal.ReviewedAt, proposal.ReviewedByUserId) = prior == HoldProposalStatus.Pending
                        ? (null, null, null)
                        : (proposal.HoldId, proposal.ReviewedAt, proposal.ReviewedByUserId);
                }

                break;
            case WallEnhancementItemKind.Link:
                var (a, b) = HoldLinkPairSuggestion.Key(left, right);
                db.HoldLinkSuggestions.RemoveRange(await db.HoldLinkSuggestions.Where(s => s.WallId == wallId && s.HoldAId == a && s.HoldBId == b).ToListAsync(ct));
                if (item.PriorState!.StartsWith(PendingLinkPrefix, StringComparison.Ordinal)
                    && double.TryParse(item.PriorState[PendingLinkPrefix.Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out var mm))
                {
                    db.HoldLinkSuggestions.Add(new HoldLinkSuggestion { WallId = wallId, HoldAId = a, HoldBId = b, DistanceMm = mm, Status = HoldLinkSuggestionStatus.Pending });
                }

                break;
            case WallEnhancementItemKind.Duplicate when item.PriorState == NoRecord:
                var (da, dbId) = HoldMergeRules.PairKey(left, right);
                db.HoldDuplicateDismissals.RemoveRange(
                    await db.HoldDuplicateDismissals.Where(d => d.WallId == wallId && d.HoldAId == da && d.HoldBId == dbId).ToListAsync(ct));
                break;
        }
    }
}
