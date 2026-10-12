// <copyright file="HoldColorProposer.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Holds;
using Blocwerk.Core.Holds.Colors;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// <see cref="IHoldColorProposer"/>: works in process on the stored panel photos, one decoded photo at a time and off
/// the caller's thread, so it needs no detection service and no 3D model.
/// </summary>
public sealed partial class HoldColorProposer : IHoldColorProposer
{
    /// <inheritdoc/>
    public async Task<HoldColorPlan> PlanAsync(
        BlocwerkDbContext db, Guid wallId, IReadOnlyCollection<Guid>? onlyHoldIds = null, CancellationToken ct = default)
    {
        var live = await (await LiveWallHolds.QueryAsync(db, wallId, ct)).AsNoTracking().Where(h => !h.IsVirtual).ToListAsync(ct);
        var groups = await GroupsWithoutColorAsync(db, wallId, live, onlyHoldIds, ct);

        // A linked hold that has a colour settles its group: same physical hold, same colour. A colour somebody set
        // comes before a proposal.
        var entries = new List<HoldColorPlanEntry>();
        var open = new List<List<Hold>>();
        foreach (var group in groups)
        {
            var source = group.Where(h => !Blank(h)).OrderBy(h => h.HasProposedColor ? 1 : 0).FirstOrDefault();
            if (source is null)
            {
                open.Add(group);
                continue;
            }

            entries.AddRange(group.Where(Blank).Select(h =>
                new HoldColorPlanEntry(h.Id, source.Color, source.HasProposedColor, FromLinked: true, HoldColorVerdict.Proposed)));
        }

        var estimates = await EstimateAsync(db, wallId, open.SelectMany(g => g).ToList(), ct);
        foreach (var group in open)
        {
            var choice = LinkedHoldColorRule.Choose(group.Select(h => estimates[h.Id]).ToList());
            entries.AddRange(group.Select(h =>
                new HoldColorPlanEntry(h.Id, choice.Color, IsProposal: choice.Color is not null, FromLinked: false, choice.Verdict)));
        }

        return new HoldColorPlan(entries);
    }

    /// <inheritdoc/>
    public async Task<HoldColorProposalReport> ApplyAsync(
        BlocwerkDbContext db, Guid wallId, HoldColorPlan plan, CancellationToken ct = default)
    {
        var report = new HoldColorProposalReport();
        foreach (var unset in plan.Entries.Where(e => e.Color is null))
        {
            report.AddUnset(unset.Verdict, 1);
        }

        // The plan may be seconds old. Read what the database says now, past anything this context already tracks: a
        // hold that got a colour in between (the hold editor takes no wall lock) or is no longer live is not written.
        var planned = plan.Entries.Where(e => e.Color is not null).ToDictionary(e => e.HoldId);
        var ids = planned.Keys.ToList();
        var now = await (await LiveWallHolds.QueryAsync(db, wallId, ct)).AsNoTracking()
            .Where(h => ids.Contains(h.Id))
            .Select(h => new { h.Id, h.Color, h.Material })
            .ToDictionaryAsync(h => h.Id, ct);
        var blank = now.Values.Where(h => string.IsNullOrWhiteSpace(h.Color)).Select(h => h.Id).ToList();
        report.ChangedMeanwhile = ids.Count - blank.Count;
        foreach (var hold in await db.Holds.Where(h => blank.Contains(h.Id)).ToListAsync(ct))
        {
            var entry = planned[hold.Id];

            // A wooden hold only comes in the wood tones, which are never proposed: it stays without a colour.
            if (!HoldPalette.IsValidFor(entry.Color, now[hold.Id].Material))
            {
                report.AddUnset(HoldColorVerdict.NotForMaterial, 1);
                continue;
            }

            hold.Color = entry.Color;
            hold.ProposedColor = entry.IsProposal ? entry.Color : null;
            report.AddWritten(entry);
        }

        return report;
    }

    private static bool Blank(Hold hold) => string.IsNullOrWhiteSpace(hold.Color);

    /// <summary>
    /// The link groups (the same hold on several panels; a hold without links is its own group) that contain a wanted
    /// hold without a colour. Members are the group's live holds, with or without a colour.
    /// </summary>
    private static async Task<List<List<Hold>>> GroupsWithoutColorAsync(
        BlocwerkDbContext db, Guid wallId, List<Hold> live, IReadOnlyCollection<Guid>? onlyHoldIds, CancellationToken ct)
    {
        var byId = live.ToDictionary(h => h.Id);
        var links = (await db.HoldLinks.AsNoTracking().Where(l => l.WallId == wallId)
                .Select(l => new { l.HoldAId, l.HoldBId }).ToListAsync(ct))
            .Where(l => byId.ContainsKey(l.HoldAId) && byId.ContainsKey(l.HoldBId))
            .Select(l => new HoldLinkPair(l.HoldAId, l.HoldBId))
            .ToList();
        var only = onlyHoldIds?.ToHashSet();
        return HoldPropertySync.ConnectedComponents(byId.Keys, links)
            .Select(ids => ids.Order().Select(id => byId[id]).ToList())
            .Where(group => group.Any(h => Blank(h) && (only is null || only.Contains(h.Id))))
            .ToList();
    }
}
