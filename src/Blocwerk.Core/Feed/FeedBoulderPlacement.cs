// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Feed;

/// <summary>
/// Places the rows a mapper wrote on a boulder without knowing its wall (a comment, a tick, a video carry only a
/// <c>BoulderId</c>), as part of <see cref="FeedBoulderResolver"/>: the wall comes from a tracked boulder, or from the
/// resolver's one lookup per save. A row of a draft meant for the wall or its members goes to setters only, as the draft
/// itself does (§5.4), so share and kiosk viewers never learn a draft's id. Inside a <see cref="FeedScope"/> nothing is
/// looked up: an unknown boulder's row goes to the scope's wall, where it collapses to the resync anyway. A row whose
/// boulder is gone, or could not be looked up, is dropped, except a user's own row, which stays wall-less for
/// <c>/api/me</c>.
/// </summary>
internal static class FeedBoulderPlacement
{
    public static bool IsUnplaced(WallChange row) => row.WallId is null && row.BoulderId is not null;

    public static FeedPlacementPlan Plan(DbContext context, FeedMapContext map)
    {
        var ids = map.Rows.Where(IsUnplaced).Select(r => r.BoulderId!.Value).ToHashSet();
        if (ids.Count == 0)
        {
            return FeedPlacementPlan.None;
        }

        var known = Tracked(context, ids);
        var missing = FeedScope.Active is null ? ids.Where(id => !known.ContainsKey(id)).ToList() : [];
        return new FeedPlacementPlan(known, missing);
    }

    /// <summary>Places the unplaced rows with what the plan and the lookup (<paramref name="looked"/>) know.</summary>
    public static void Apply(FeedMapContext map, FeedPlacementPlan plan, IEnumerable<FeedBoulderFacts> looked)
    {
        if (!map.Rows.Any(IsUnplaced))
        {
            return;
        }

        var known = new Dictionary<Guid, FeedBoulderFacts>(plan.Known);
        foreach (var fact in looked.Where(f => plan.Missing.Contains(f.Id)))
        {
            known[fact.Id] = fact;
        }

        var scope = FeedScope.Active;
        foreach (var row in map.Rows.Where(IsUnplaced))
        {
            if (known.TryGetValue(row.BoulderId!.Value, out var boulder))
            {
                row.WallId = boulder.WallId;
                if (boulder.IsDraft && row.Audience is FeedAudience.Wall or FeedAudience.Members)
                {
                    row.Audience = FeedAudience.Setters;
                }
            }
            else if (scope is not null && row.Audience != FeedAudience.User)
            {
                row.WallId = scope.WallId;
            }
        }

        map.RemoveRows(r => IsUnplaced(r) && r.Audience != FeedAudience.User);
    }

    // The save's own boulders, without a second DetectChanges: the collector has just run one.
    private static Dictionary<Guid, FeedBoulderFacts> Tracked(DbContext context, HashSet<Guid> ids)
    {
        var detect = context.ChangeTracker.AutoDetectChangesEnabled;
        context.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            return context.ChangeTracker.Entries<Boulder>()
                .Where(e => ids.Contains(e.Entity.Id))
                .Select(e => e.Entity)
                .DistinctBy(b => b.Id)
                .ToDictionary(b => b.Id, b => new FeedBoulderFacts(b.Id, b.WallId, b.IsDraft, b.Generation));
        }
        finally
        {
            context.ChangeTracker.AutoDetectChangesEnabled = detect;
        }
    }
}
