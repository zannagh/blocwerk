// <copyright file="LookOverListing.Queue.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;

namespace Blocwerk.Web.Components.Shared.LookOver;

/// <summary>
/// The card deck's one queue: every open finding, asked about one at a time. What a boulder uses comes first, then
/// the groups in the order they matter (the holds the update could not find, then the kinds as
/// <see cref="LookOverRules.Order"/> ranks them), and within a group the order the page has kept since it opened, so
/// answering a card never reshuffles what comes next. The discarded detections come last, those most likely to be a
/// missed hold first (the service's order); the ones a hold on the wall covers are only in their own queue.
/// </summary>
public sealed partial class LookOverListing
{
    /// <summary>The open findings of a filter, in the order the deck asks about them.</summary>
    /// <param name="filter">Which findings.</param>
    /// <param name="boulderId">Only findings on a hold of this boulder.</param>
    /// <returns>The queue.</returns>
    public IReadOnlyList<LookOverFindingView> Queue(LookOverFilter filter, Guid? boulderId) =>
        Findings
            .Where(f => f.Status == LookOverFindingStatus.Open && Shown(f, filter.OnBouldersOnly, boulderId) && InGroup(f, filter))
            .OrderBy(f => OnBoulder(f) ? 0 : 1)
            .ThenBy(f => f.Missing ? -1 : LookOverRules.Rank(f.Kind))
            .ThenBy(f => f.Missing ? missingOrder.IndexOf(f.Id) : order.IndexOf(f.Id))
            .ToList();

    /// <summary>
    /// The chips above the deck: everything, what is on boulders, then one per group that has something open. Each
    /// counts what its queue would hold.
    /// </summary>
    /// <param name="boulderId">Only findings on a hold of this boulder.</param>
    /// <returns>The chips; "all" is always the first.</returns>
    public IReadOnlyList<LookOverChip> Chips(Guid? boulderId)
    {
        var chips = new List<LookOverChip> { new(LookOverFilter.All, "All", Queue(LookOverFilter.All, boulderId).Count) };
        void Add(LookOverFilter filter, string label)
        {
            var open = Queue(filter, boulderId).Count;
            if (open > 0)
            {
                chips.Add(new LookOverChip(filter, label, open));
            }
        }

        if (boulderId is null)
        {
            Add(LookOverFilter.OnBoulders, "On boulders");
        }

        Add(LookOverFilter.Missing, LookOverCopy.MissingChip);
        foreach (var kind in LookOverRules.Order)
        {
            Add(new LookOverFilter(Kind: kind), LookOverCopy.ChipLabel(kind));
        }

        Add(LookOverFilter.Duplicates, LookOverCopy.DuplicatesChip);
        return chips;
    }

    /// <summary>The open discarded detections a hold on the wall covers: what "not a hold for all" is about.</summary>
    /// <returns>Their ids.</returns>
    public IReadOnlyList<Guid> DuplicateIds() =>
        Findings.Where(f => f.Status == LookOverFindingStatus.Open && f.ProbablyDuplicate).Select(f => f.Id).ToList();

    /// <summary>The open discarded detections that are likely a missed hold (sure, on no hold): what "add all" is about.</summary>
    /// <returns>Their ids.</returns>
    public IReadOnlyList<Guid> LikelyMissedIds() =>
        Findings.Where(f => f is { Status: LookOverFindingStatus.Open, Discarded: true, CountsAsOpen: true }).Select(f => f.Id).ToList();

    private static bool InGroup(LookOverFindingView finding, LookOverFilter filter)
    {
        if (filter.DuplicatesOnly || finding.ProbablyDuplicate)
        {
            return filter.DuplicatesOnly && finding.ProbablyDuplicate;
        }

        if (filter.MissingOnly)
        {
            return finding.Missing;
        }

        return filter.Kind is not { } kind || (!finding.Missing && finding.Kind == kind);
    }
}
