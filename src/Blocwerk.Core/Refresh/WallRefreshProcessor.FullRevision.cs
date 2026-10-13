// <copyright file="WallRefreshProcessor.FullRevision.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Refresh;

/// <summary>
/// The confirm screen's facts for a full revision (<see cref="WallUpdateSession.FullRevision"/>): what is detected per
/// panel, what is created, and what the apply retires and archives. Read from the wall as it is now, so the screen
/// names the boulders the apply will archive.
/// </summary>
public sealed partial class WallRefreshProcessor
{
    private async Task<RefreshFullRevision?> FullRevisionFactsAsync(Guid wallId, QuickDecisions quick, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var open = await db.WallUpdateSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.WallId == wallId && s.Status == WallUpdateSessionStatus.Open, ct);
        if (open is not { FullRevision: true })
        {
            return null;
        }

        var staged = await db.WallPanels.AsNoTracking()
            .Where(p => p.WallId == wallId && p.Generation == open.StagedGeneration && p.StagedPhoto != null)
            .Select(p => new { p.Id, p.Col, p.Row })
            .ToListAsync(ct);
        var byPanel = await StagedHoldsByPanelAsync(wallId, ct);
        var lines = staged
            .OrderBy(p => p is { Col: 0, Row: 0 } ? 0 : 1).ThenBy(p => p.Row).ThenBy(p => p.Col)
            .Select(p => new RevisionPanelLine(PanelPositionName.Describe(p.Col, p.Row), byPanel.GetValueOrDefault(p.Id)?.Count ?? 0))
            .ToList();
        var neighbours = quick.Neighbours.Sum(n => (byPanel.GetValueOrDefault(n.PanelId)?.Count ?? 0) - n.RemovedNeighbourHoldIds.Count);
        var retired = await (await LiveWallHolds.QueryAsync(db, wallId, ct)).CountAsync(h => !h.IsVirtual, ct);
        var boulders = await db.Boulders.AsNoTracking()
            .Where(b => b.WallId == wallId && !b.IsArchived)
            .OrderBy(b => b.Name)
            .Select(b => b.Name)
            .ToListAsync(ct);
        return new RefreshFullRevision(lines, quick.AcceptedNewCentreHoldIds.Count + neighbours, retired, boulders);
    }
}
