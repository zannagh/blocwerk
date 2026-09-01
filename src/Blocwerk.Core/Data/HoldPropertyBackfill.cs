using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Data;

/// <summary>
/// One-time propagation of appearance/identity fields (Color, Material, Category, HandType) across
/// linked holds — the same physical hold seen on overlapping big-wall panels, tied by
/// <see cref="HoldLink"/>. The most-central hold in each link's connected component is the source of
/// truth and every peripheral copy inherits its values verbatim (centre wins fully, nulls included).
/// Idempotent: it only writes a field that actually differs and only saves when something changed, so
/// a second run is a pure no-op. Never touches geometry, position, or lifecycle fields.
/// </summary>
public static class HoldPropertyBackfill
{
    public static async Task RunIfNeededAsync(IDbContextFactory<BlocwerkDbContext> factory, ILogger logger)
    {
        await using var db = await factory.CreateDbContextAsync();

        var links = await db.HoldLinks
            .Select(l => new HoldLinkPair(l.HoldAId, l.HoldBId))
            .ToListAsync();
        if (links.Count == 0)
        {
            return;
        }

        // Links only ever connect holds on the same wall, so a global connected-components pass never
        // merges holds across walls — no per-wall grouping is needed.
        var holdIds = links.SelectMany(l => new[] { l.HoldAId, l.HoldBId }).Distinct().ToList();

        var holds = await db.Holds
            .Where(h => holdIds.Contains(h.Id))
            .ToListAsync();
        var holdsById = holds.ToDictionary(h => h.Id);

        var panelById = await LoadPanelPositionsAsync(db, holds);
        var components = HoldPropertySync.ConnectedComponents(holdsById.Keys, links);

        var changedHolds = 0;
        var changedComponents = 0;
        foreach (var component in components)
        {
            if (component.Count < 2)
            {
                continue;
            }

            var candidates = component
                .Where(id => holdsById.ContainsKey(id))
                .Select(id => ToCentrality(holdsById[id], panelById))
                .ToList();
            if (candidates.Count < 2)
            {
                continue;
            }

            var sourceId = HoldPropertySync.MostCentral(candidates).HoldId;
            var source = holdsById[sourceId];

            var componentChanged = false;
            foreach (var id in component)
            {
                if (id == sourceId || !holdsById.TryGetValue(id, out var target))
                {
                    continue;
                }

                if (HoldPropertySync.CopyAppearance(source, target))
                {
                    changedHolds++;
                    componentChanged = true;
                }
            }

            if (componentChanged)
            {
                changedComponents++;
            }
        }

        if (changedHolds == 0)
        {
            logger.LogInformation("Hold appearance backfill: nothing to do.");
            return;
        }

        await db.SaveChangesAsync();
        logger.LogInformation(
            "Propagated hold appearance across {HoldCount} holds in {ComponentCount} linked components.",
            changedHolds, changedComponents);
    }

    private static async Task<Dictionary<Guid, (int Col, int Row)>> LoadPanelPositionsAsync(
        BlocwerkDbContext db,
        IEnumerable<Hold> holds)
    {
        var panelIds = holds
            .Where(h => h.WallPanelId is not null)
            .Select(h => h.WallPanelId!.Value)
            .Distinct()
            .ToList();
        if (panelIds.Count == 0)
        {
            return new Dictionary<Guid, (int, int)>();
        }

        var panels = await db.WallPanels
            .Where(p => panelIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Col, p.Row })
            .ToListAsync();
        return panels.ToDictionary(p => p.Id, p => (p.Col, p.Row));
    }

    private static HoldCentrality ToCentrality(Hold hold, IReadOnlyDictionary<Guid, (int Col, int Row)> panelById)
    {
        if (hold.WallPanelId is { } panelId && panelById.TryGetValue(panelId, out var pos))
        {
            return new HoldCentrality(hold.Id, pos.Col, pos.Row);
        }

        return new HoldCentrality(hold.Id, null, null);
    }
}
