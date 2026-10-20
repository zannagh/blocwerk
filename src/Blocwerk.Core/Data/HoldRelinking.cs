// <copyright file="HoldRelinking.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Data;

/// <summary>
/// When two hold rows become one, the hold-to-hold facts of the one that goes are moved onto the one that stays instead
/// of being dropped (<see cref="HoldDeletion"/> drops links and tombstones lineage, which is right for a hold that is
/// really gone, not for one that lives on as part of another). Used by the duplicate-virtual join and the editor's merge
/// tool. Nothing here saves: the caller commits in the same unit of work as the delete.
/// </summary>
internal static class HoldRelinking
{
    /// <summary>
    /// Re-points every <see cref="HoldLink"/> off the duplicate onto the survivor. A HoldLink is the
    /// curated "same physical hold on two adjacent panels" fact (PanelLinkTool), not an alignment
    /// artifact: dropping it would silently unlink the twin on the neighbouring panel and stop
    /// <see cref="BoulderHoldReconciler"/> expanding boulder membership to it. A link between the two
    /// merged holds would collapse into a self-link, and a link the survivor already has (unordered,
    /// as the unique index on <c>(HoldAId, HoldBId)</c> demands) would collide — both are dropped.
    /// </summary>
    public static async Task RepointHoldLinksAsync(
        BlocwerkDbContext db, Guid survivorHoldId, Guid duplicateHoldId, CancellationToken ct)
    {
        var links = await db.HoldLinks
            .Where(l => l.HoldAId == duplicateHoldId || l.HoldBId == duplicateHoldId
                || l.HoldAId == survivorHoldId || l.HoldBId == survivorHoldId)
            .ToListAsync(ct);

        var touchesDuplicate = links
            .Where(l => l.HoldAId == duplicateHoldId || l.HoldBId == duplicateHoldId)
            .ToList();
        var taken = links
            .Except(touchesDuplicate)
            .Select(l => UnorderedPair(l.HoldAId, l.HoldBId))
            .ToHashSet();

        foreach (var link in touchesDuplicate)
        {
            var other = link.HoldAId == duplicateHoldId ? link.HoldBId : link.HoldAId;
            if (other == survivorHoldId || other == duplicateHoldId
                || !taken.Add(UnorderedPair(survivorHoldId, other)))
            {
                db.HoldLinks.Remove(link);
                continue;
            }

            if (link.HoldAId == duplicateHoldId)
            {
                link.HoldAId = survivorHoldId;
            }
            else
            {
                link.HoldBId = survivorHoldId;
            }
        }
    }

    /// <summary>
    /// Re-points every <see cref="HoldGenerationLink"/> off the duplicate onto the survivor. These rows
    /// are cross-generation lineage ("this gen-N hold became that gen-N+1 hold"), the only record of
    /// where a carried-forward hold came from, so they are preserved rather than deleted: a predecessor
    /// whose successor was the duplicate now points at the survivor, and a successor descended from the
    /// duplicate now descends from the survivor. Both holds sit on the SAME generation (guarded on
    /// load), so the stored From/To generations stay truthful. A row that would collapse onto a single
    /// hold, or duplicate one the survivor already has — the unique index on
    /// <c>(OldHoldId, NewHoldId)</c> forbids that — is dropped instead.
    /// </summary>
    public static async Task RepointGenerationLinksAsync(
        BlocwerkDbContext db, Guid survivorHoldId, Guid duplicateHoldId, CancellationToken ct)
    {
        var links = await db.HoldGenerationLinks
            .Where(l => l.OldHoldId == duplicateHoldId || l.NewHoldId == duplicateHoldId
                || l.OldHoldId == survivorHoldId || l.NewHoldId == survivorHoldId)
            .ToListAsync(ct);

        var touchesDuplicate = links
            .Where(l => l.OldHoldId == duplicateHoldId || l.NewHoldId == duplicateHoldId)
            .ToList();

        // Only rows with BOTH ends live take part in the uniqueness dedupe: a tombstoned end is NULL
        // and the unique index is filtered to exclude those. The consequence is that duplicate
        // tombstones — several (NULL, survivor) rows — can accumulate here and are indistinguishable
        // from one another, so nothing can ever collapse them: they are unbounded noise. Tolerated
        // because nothing outside the dev endpoints reads lineage today; if that changes, these rows
        // need a real identity (or a dedupe) rather than a comment.
        var taken = links
            .Except(touchesDuplicate)
            .Where(l => l.OldHoldId is not null && l.NewHoldId is not null)
            .Select(l => (l.OldHoldId!.Value, l.NewHoldId!.Value))
            .ToHashSet();

        foreach (var link in touchesDuplicate)
        {
            var oldHoldId = link.OldHoldId == duplicateHoldId ? survivorHoldId : link.OldHoldId;
            var newHoldId = link.NewHoldId == duplicateHoldId ? survivorHoldId : link.NewHoldId;

            if (oldHoldId is { } live && newHoldId is { } liveNew
                && (live == liveNew || !taken.Add((live, liveNew))))
            {
                db.HoldGenerationLinks.Remove(link);
                continue;
            }

            link.OldHoldId = oldHoldId;
            link.NewHoldId = newHoldId;
        }
    }

    /// <summary>
    /// Orders a hold pair so the unordered pair (A,B) and (B,A) share one key, matching how the panel
    /// link tool dedupes links.
    /// </summary>
    private static (Guid First, Guid Second) UnorderedPair(Guid a, Guid b)
    {
        return a.CompareTo(b) <= 0 ? (a, b) : (b, a);
    }
}
