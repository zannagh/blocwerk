using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// Read-only topology queries, panel photo serving, and the pure helpers (grid neighbours,
/// matcher-hold construction, overlap direction) for <see cref="WallPanelService"/>.
/// </summary>
public partial class WallPanelService
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<WallPanelInfo>> GetPanelsAsync(Guid wallId)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        // A centre/neighbour update adds a NEW panel row at the next generation and promotes it, but the
        // superseded row keeps its Photo. Without deduping we'd surface two live panels at the same
        // (Col,Row) — the stale one can win and show the old image with no holds. Keep only the latest
        // generation per position so each grid cell resolves to its current panel.
        var panels = await db.WallPanels
            .AsNoTracking()
            .Where(p => p.WallId == wallId && (p.Photo != null || p.StagedPhoto != null))
            .Select(p => new { p.Id, p.Col, p.Row, p.Generation, HasLive = p.Photo != null, HasStaged = p.StagedPhoto != null })
            .ToListAsync();

        return panels
            .GroupBy(p => (p.Col, p.Row))
            // Prefer the latest LIVE panel for the cell. Live-first matters mid-update: a staged row
            // sits one generation ahead of the live one, so ordering by generation alone would let the
            // not-yet-live staged panel win and the live viewers (which filter on IsLive) would drop
            // the cell. Only when a cell has no live panel at all does the latest staged row stand in.
            .Select(g => g.OrderByDescending(p => p.HasLive).ThenByDescending(p => p.Generation).First())
            .OrderBy(p => p.Row).ThenBy(p => p.Col)
            .Select(p => new WallPanelInfo(p.Id, p.Col, p.Row, p.HasLive, p.HasStaged))
            .ToList();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PanelPosition>> GetFrontierPositionsAsync(Guid wallId)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var placements = await db.WallPanels
            .AsNoTracking()
            .Where(p => p.WallId == wallId)
            .Select(p => new { p.Col, p.Row, Live = p.Photo != null })
            .ToListAsync();

        var occupied = placements.Select(p => (p.Col, p.Row)).ToHashSet();
        var frontier = new HashSet<(int Col, int Row)>();
        foreach (var live in placements.Where(p => p.Live))
        {
            foreach (var (c, r) in Neighbors(live.Col, live.Row))
            {
                if (!occupied.Contains((c, r)))
                {
                    frontier.Add((c, r));
                }
            }
        }

        return frontier.Select(f => new PanelPosition(f.Col, f.Row)).ToList();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<HoldLinkPair>> GetHoldLinksAsync(Guid wallId)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        // Setting CurrentUserId applies the same visibility filters the other reads rely on: a
        // wall the caller cannot see yields no link rows.
        return await db.HoldLinks
            .AsNoTracking()
            .Where(l => l.WallId == wallId)
            .Select(l => new HoldLinkPair(l.HoldAId, l.HoldBId))
            .ToListAsync();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PanelHold>> GetPanelHoldsAsync(Guid wallId, Guid panelId, bool includeStaged)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        // Setting CurrentUserId applies the same visibility filters the other reads rely on: a
        // wall the caller cannot see yields no panel row and therefore no holds.
        var panel = await db.WallPanels
            .AsNoTracking()
            .Where(p => p.Id == panelId && p.WallId == wallId)
            .Select(p => new { HasLive = p.Photo != null })
            .FirstOrDefaultAsync();
        if (panel is null || (!includeStaged && !panel.HasLive))
        {
            return [];
        }

        var generation = await db.Walls
            .AsNoTracking()
            .Where(w => w.Id == wallId)
            .Select(w => (int?)w.CurrentGeneration)
            .FirstOrDefaultAsync();
        if (generation is null)
        {
            return [];
        }

        // Staged holds from an in-flight big-wall update live one generation ahead of the live wall
        // (see WallBigUpdateService: stagedGen = CurrentGeneration + 1). When the caller asks for the
        // staged view we must read that generation — otherwise the update UI (carryover re-target, the
        // new panel overlap stepper) gets an empty list and shows no overlay on the staged image.
        var effectiveGeneration = includeStaged ? generation.Value + 1 : generation.Value;

        return await db.Holds
            .AsNoTracking()
            .Where(h => h.WallPanelId == panelId && h.Generation == effectiveGeneration)
            .Select(h => new PanelHold(h.Id, h.X, h.Y, h.Radius, h.Color, h.ShapePoints, h.Name, h.Material, h.Category, h.HandType))
            .ToListAsync();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Hold>> GetPanelHoldEntitiesAsync(Guid wallId, Guid panelId)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        // Setting CurrentUserId applies the same visibility filters the other reads rely on: a
        // wall the caller cannot see yields no panel row and therefore no holds.
        var panel = await db.WallPanels
            .AsNoTracking()
            .Where(p => p.Id == panelId && p.WallId == wallId)
            .Select(p => new { HasLive = p.Photo != null })
            .FirstOrDefaultAsync();
        if (panel is null || !panel.HasLive)
        {
            return [];
        }

        var generation = await db.Walls
            .AsNoTracking()
            .Where(w => w.Id == wallId)
            .Select(w => (int?)w.CurrentGeneration)
            .FirstOrDefaultAsync();
        if (generation is null)
        {
            return [];
        }

        // The live generation only (includeStaged:false semantics): per-panel editing works on the
        // live wall, not an in-flight staged update. Full entities, no projection — the editor needs
        // the complete Hold (shape points, colour, category, material) to hand out editable clones.
        return await db.Holds
            .AsNoTracking()
            .Where(h => h.WallPanelId == panelId && h.Generation == generation.Value)
            .ToListAsync();
    }

    /// <inheritdoc/>
    public Task<WallPhoto?> GetPanelPhotoAsync(Guid wallId, Guid panelId) =>
        GetPanelBytesAsync(wallId, panelId, staged: false);

    /// <inheritdoc/>
    public Task<WallPhoto?> GetPanelStagedPhotoAsync(Guid wallId, Guid panelId) =>
        GetPanelBytesAsync(wallId, panelId, staged: true);

    private async Task<WallPhoto?> GetPanelBytesAsync(Guid wallId, Guid panelId, bool staged)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync();
        var row = await db.WallPanels
            .AsNoTracking()
            .Where(p => p.Id == panelId && p.WallId == wallId)
            .Select(p => staged
                ? new { Bytes = p.StagedPhoto, Type = p.StagedPhotoContentType }
                : new { Bytes = p.Photo, Type = p.PhotoContentType })
            .FirstOrDefaultAsync();

        return row?.Bytes is null ? null : new WallPhoto(row.Bytes, row.Type);
    }

    private static (List<MatcherHold> Holds, Guid[] IndexToGuid) BuildMatcherHolds(IReadOnlyList<Hold> holds)
    {
        var matcher = new List<MatcherHold>(holds.Count);
        var index = new Guid[holds.Count];
        for (var i = 0; i < holds.Count; i++)
        {
            var h = holds[i];
            index[i] = h.Id;
            // Appearance descriptors are sampled from the image by the matcher, gated on SizeNorm,
            // so pass the normalized hold radius as the size. Colour is left null: our detector
            // yields a categorical colour name, not the CIE-Lab the matcher's colour tie-break needs.
            matcher.Add(new MatcherHold(i, h.X, h.Y, h.Radius));
        }

        return (matcher, index);
    }

    private static HoldOverlapDirection DirectionFromNeighbor(int neighborCol, int neighborRow, int col, int row)
    {
        if (neighborCol < col)
        {
            return HoldOverlapDirection.Right;
        }

        if (neighborCol > col)
        {
            return HoldOverlapDirection.Left;
        }

        if (neighborRow < row)
        {
            return HoldOverlapDirection.Down;
        }

        return HoldOverlapDirection.Up;
    }

    private static IEnumerable<(int Col, int Row)> Neighbors(int col, int row)
    {
        yield return (col - 1, row);
        yield return (col + 1, row);
        yield return (col, row - 1);
        yield return (col, row + 1);
    }

    private static (Guid, Guid) Unordered(Guid a, Guid b) =>
        a.CompareTo(b) <= 0 ? (a, b) : (b, a);
}
