using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// The neighbour-overlap half of the wall-update session service: persisting one staged panel's
/// confirmed links and removals as the user walks the panels, so an interrupted walk resumes at the
/// panel it stopped on with every earlier panel already decided.
/// </summary>
public partial class WallUpdateSessionService
{
    /// <inheritdoc/>
    public async Task SaveNeighbourLinkSetAsync(Guid wallId, NeighbourLinkSet linkSet)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);

        var session = await RequireOpenAsync(db, wallId);
        await ReplacePanelRowsAsync(db, session.Id, linkSet.PanelId);

        // Same defence as the carryover save: an id whose hold was deleted from the staging meanwhile
        // would break the FK, and the row would have cascaded away anyway.
        var candidates = linkSet.Links.SelectMany(l => new[] { l.NeighborHoldId, l.NewHoldId })
            .Concat(linkSet.RemovedNeighbourHoldIds)
            .ToList();
        var live = await LoadLiveHoldIdsAsync(db, wallId, candidates);

        AddLinkRows(db, session.Id, linkSet, live);
        AddRemovalRows(db, session.Id, linkSet, live);

        WallUpdateSessions.Touch(session, user.Id);
        await db.SaveChangesAsync();

        logger.LogDebug(
            "Wall update session {SessionId} panel {PanelId} confirmed by {UserId}: {Links} link(s), {Removed} removal(s)",
            session.Id, linkSet.PanelId, user.Id, linkSet.Links.Count, linkSet.RemovedNeighbourHoldIds.Count);
    }

    /// <summary>Writes the panel's confirmed correspondences, skipping any end that no longer exists.</summary>
    private static void AddLinkRows(
        BlocwerkDbContext db, Guid sessionId, NeighbourLinkSet linkSet, IReadOnlySet<Guid> live)
    {
        foreach (var link in linkSet.Links)
        {
            if (!live.Contains(link.NeighborHoldId) || !live.Contains(link.NewHoldId))
            {
                continue;
            }

            db.WallUpdateNeighbourDecisions.Add(new WallUpdateNeighbourDecision
            {
                SessionId = sessionId,
                Kind = WallUpdateNeighbourDecisionKind.Link,
                PanelId = linkSet.PanelId,
                HoldId = link.NewHoldId,
                CentreHoldId = link.NeighborHoldId,
                Moved = link.Moved,
            });
        }
    }

    /// <summary>Writes the panel's holds the user marked physically absent.</summary>
    private static void AddRemovalRows(
        BlocwerkDbContext db, Guid sessionId, NeighbourLinkSet linkSet, IReadOnlySet<Guid> live)
    {
        foreach (var holdId in linkSet.RemovedNeighbourHoldIds.Distinct())
        {
            if (!live.Contains(holdId))
            {
                continue;
            }

            db.WallUpdateNeighbourDecisions.Add(new WallUpdateNeighbourDecision
            {
                SessionId = sessionId,
                Kind = WallUpdateNeighbourDecisionKind.Removed,
                PanelId = linkSet.PanelId,
                HoldId = holdId,
            });
        }
    }

    /// <summary>
    /// Removes a panel's existing rows so the caller can write the current set. A panel's outcome is
    /// always rewritten whole — the stepper hands back its complete decision, never a delta.
    /// </summary>
    private static async Task ReplacePanelRowsAsync(BlocwerkDbContext db, Guid sessionId, Guid panelId)
    {
        var existing = await db.WallUpdateNeighbourDecisions
            .Where(d => d.SessionId == sessionId && d.PanelId == panelId)
            .ToListAsync();
        if (existing.Count > 0)
        {
            db.WallUpdateNeighbourDecisions.RemoveRange(existing);
        }
    }
}
