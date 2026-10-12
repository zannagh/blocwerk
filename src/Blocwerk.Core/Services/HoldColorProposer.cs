// <copyright file="HoldColorProposer.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Holds.Colors;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// <see cref="IHoldColorProposer"/>: works in process on the stored panel photos, one decoded photo at a time, so it
/// needs no detection service and no 3D model.
/// </summary>
public sealed class HoldColorProposer : IHoldColorProposer
{
    /// <inheritdoc/>
    public async Task<HoldColorProposalReport> ProposeAsync(
        BlocwerkDbContext db, Guid wallId, IReadOnlyCollection<Guid>? onlyHoldIds = null, CancellationToken ct = default)
    {
        var report = new HoldColorProposalReport();
        var live = await (await LiveWallHolds.QueryAsync(db, wallId, ct)).Where(h => !h.IsVirtual).ToListAsync(ct);
        var groups = await GroupsWithoutColorAsync(db, wallId, live, onlyHoldIds, ct);

        // A linked hold that has a colour settles its group: same physical hold, same colour.
        var open = new List<List<Hold>>();
        foreach (var group in groups)
        {
            var source = group.Where(h => !Blank(h)).OrderBy(h => h.HasProposedColor ? 1 : 0).FirstOrDefault();
            if (source is null)
            {
                open.Add(group);
                continue;
            }

            foreach (var hold in group.Where(Blank))
            {
                hold.Color = source.Color;
                hold.ProposedColor = source.ProposedColor;
                report.FromLinked++;
            }
        }

        var estimates = await EstimateAsync(db, wallId, open.SelectMany(g => g).ToList(), ct);
        foreach (var group in open)
        {
            var choice = LinkedHoldColorRule.Choose(group.Select(h => estimates[h.Id]).ToList());
            if (choice.Color is null)
            {
                report.AddUnset(choice.Verdict, group.Count);
                continue;
            }

            report.AddProposed(choice.Color, group.Count);
            foreach (var hold in group)
            {
                hold.Color = choice.Color;
                hold.ProposedColor = choice.Color;
            }
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

    /// <summary>Estimates every hold on the live photo of its panel (the wall photo for a hold without a panel).</summary>
    private static async Task<Dictionary<Guid, HoldColorEstimate>> EstimateAsync(
        BlocwerkDbContext db, Guid wallId, List<Hold> holds, CancellationToken ct)
    {
        var estimates = new Dictionary<Guid, HoldColorEstimate>();
        foreach (var onPhoto in holds.GroupBy(h => h.WallPanelId))
        {
            var photo = onPhoto.Key is { } panelId
                ? await db.WallPanels.AsNoTracking().Where(p => p.Id == panelId).Select(p => p.Photo).FirstOrDefaultAsync(ct)
                : await db.Walls.IgnoreQueryFilters().AsNoTracking().Where(w => w.Id == wallId).Select(w => w.Photo).FirstOrDefaultAsync(ct);
            using var pixels = HoldPhotoPixels.TryOpen(photo);
            foreach (var hold in onPhoto)
            {
                estimates[hold.Id] = pixels is null
                    ? HoldColorEstimate.Unset(HoldColorVerdict.NoPhoto)
                    : HoldColorSampler.Estimate(hold, pixels.Width, pixels.Height, pixels.At);
            }
        }

        return estimates;
    }
}
