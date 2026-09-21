using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Data;

/// <summary>
/// Startup propagation of appearance/identity fields (Name, Color, Material, HandType) across linked
/// holds — the same physical hold seen on overlapping big-wall panels, tied by <see cref="HoldLink"/>.
/// Per component and per field, the value is taken from whichever member HAS one and written onto the
/// members that have none (<see cref="HoldPropertySync.FillGapsAcrossComponent"/>).
/// <para>
/// GAP-FILLING, not overwriting, and that is the point: this runs unattended on every start, so any
/// source-of-truth pick it makes is nobody's decision. Overwriting would silently REVERT a deliberate
/// edit made on a peripheral panel at the next restart — the backfill cannot tell a value the user just
/// set on the periphery from a stale one. Live edits (edited hold wins) and link creation (the more
/// central end wins) stay full overwrites: those are user actions with an obvious author.
/// </para>
/// <para>
/// DIRECTION-AGNOSTIC, because strictly centre-outward filling does not converge: it can only move a
/// value out of the single most-central hold, so a legacy component whose CENTRE is the uncurated end
/// stays inconsistent for ever and two peripheral panels never see each other's values. Since a gap fill
/// never overwrites, privileging the centre buys nothing — centrality is kept only as the TIE-BREAK
/// when several members disagree, which keeps the result deterministic across runs.
/// </para>
/// <para>
/// <see cref="Hold.Category"/> is NOT propagated here. It is non-nullable with Hand = 0, so a hold the
/// user deliberately set back to Hand is indistinguishable from one that was never touched; filling it
/// made every restart overwrite that choice from a linked twin. It moves on a user edit only.
/// </para>
/// Idempotent: it only writes a field that is actually unset and only saves when something changed, so
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

            // Most-central first, so a field several members disagree about resolves to the central one.
            var ordered = HoldPropertySync.ByCentrality(candidates)
                .Select(c => holdsById[c.HoldId])
                .ToList();

            var componentChangedHolds = HoldPropertySync.FillGapsAcrossComponent(ordered);
            if (componentChangedHolds > 0)
            {
                changedHolds += componentChangedHolds;
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
