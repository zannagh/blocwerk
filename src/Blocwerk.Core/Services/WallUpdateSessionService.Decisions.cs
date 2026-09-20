using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// The decision half of the wall-update session service: reading every recorded decision back as a
/// promote-ready <see cref="BigUpdateConfirmation"/>, and the incremental writes that put them there.
/// </summary>
public partial class WallUpdateSessionService
{
    /// <inheritdoc/>
    public async Task<BigUpdateConfirmation> GetDecisionsAsync(Guid wallId)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);

        var session = await WallUpdateSessions.FindOpenAsync(db, wallId);
        if (session is null)
        {
            return new BigUpdateConfirmation([], [], [], []);
        }

        var holdRows = await db.WallUpdateHoldDecisions
            .Where(d => d.SessionId == session.Id)
            .ToListAsync();
        var neighbourRows = await db.WallUpdateNeighbourDecisions
            .Where(d => d.SessionId == session.Id)
            .ToListAsync();

        var carryover = holdRows
            .Where(d => d.Kind == WallUpdateHoldDecisionKind.Carry)
            .Select(d => new CarryoverDecision(d.HoldId, d.CarryKind, d.PairedHoldId))
            .ToList();
        var newCentre = holdRows.Where(d => d.Kind == WallUpdateHoldDecisionKind.NewCentreHold).ToList();

        return new BigUpdateConfirmation(
            carryover,
            newCentre.Where(d => !d.Discarded).Select(d => d.HoldId).ToList(),
            newCentre.Where(d => d.Discarded).Select(d => d.HoldId).ToList(),
            BuildNeighbourSets(neighbourRows));
    }

    /// <summary>Groups the flat neighbour rows back into one <see cref="NeighbourLinkSet"/> per panel.</summary>
    private static List<NeighbourLinkSet> BuildNeighbourSets(List<WallUpdateNeighbourDecision> rows)
    {
        return rows
            .GroupBy(d => d.PanelId)
            .Select(g => new NeighbourLinkSet(
                g.Key,
                g.Where(d => d.Kind == WallUpdateNeighbourDecisionKind.Link && d.CentreHoldId is not null)
                    .Select(d => new ConfirmedLink(d.CentreHoldId!.Value, d.HoldId, d.Moved))
                    .ToList(),
                g.Where(d => d.Kind == WallUpdateNeighbourDecisionKind.Removed)
                    .Select(d => d.HoldId)
                    .ToList()))
            .ToList();
    }

    /// <inheritdoc/>
    public async Task SaveCarryDecisionAsync(Guid wallId, CarryoverDecision decision)
    {
        await UpsertHoldDecisionAsync(
            wallId,
            WallUpdateHoldDecisionKind.Carry,
            decision.OldHoldId,
            row =>
            {
                row.CarryKind = decision.Kind;
                row.PairedHoldId = decision.Kind == CarryKind.Removed ? null : decision.NewHoldId;
            });
    }

    /// <inheritdoc/>
    public async Task SaveNewCentreHoldDecisionAsync(Guid wallId, Guid stagedHoldId, bool discarded)
    {
        await UpsertHoldDecisionAsync(
            wallId,
            WallUpdateHoldDecisionKind.NewCentreHold,
            stagedHoldId,
            row => row.Discarded = discarded);
    }

    /// <summary>
    /// Inserts or updates the one row for (session, kind, subject hold) and commits. The unique index on
    /// that triple is what makes this an upsert rather than an append.
    /// </summary>
    private async Task UpsertHoldDecisionAsync(
        Guid wallId, WallUpdateHoldDecisionKind kind, Guid holdId, Action<WallUpdateHoldDecision> apply)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);

        var session = await RequireOpenAsync(db, wallId);
        var row = await db.WallUpdateHoldDecisions.FirstOrDefaultAsync(d =>
            d.SessionId == session.Id && d.Kind == kind && d.HoldId == holdId);
        if (row is null)
        {
            row = new WallUpdateHoldDecision { SessionId = session.Id, Kind = kind, HoldId = holdId };
            db.WallUpdateHoldDecisions.Add(row);
        }

        apply(row);
        row.UpdatedAt = DateTimeOffset.UtcNow;
        WallUpdateSessions.Touch(session, user.Id);
        await db.SaveChangesAsync();
    }

    /// <inheritdoc/>
    public async Task SaveCarryOutcomeAsync(
        Guid wallId,
        IReadOnlyList<CarryoverDecision> carryover,
        IReadOnlyList<Guid> acceptedNewCentreHoldIds,
        IReadOnlyList<Guid> removedNewCentreHoldIds)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);

        var session = await RequireOpenAsync(db, wallId);
        var existing = await db.WallUpdateHoldDecisions
            .Where(d => d.SessionId == session.Id)
            .ToListAsync();
        db.WallUpdateHoldDecisions.RemoveRange(existing);

        // A hold another admin deleted from the staging since the browser last looked would fail the FK
        // and take the whole save down. Drop those ids instead: the decision is about a hold that no
        // longer exists, which is exactly what the cascade does to already-saved rows.
        var live = await LoadLiveHoldIdsAsync(db, wallId, CollectHoldIds(carryover, acceptedNewCentreHoldIds, removedNewCentreHoldIds));
        foreach (var decision in carryover)
        {
            if (!live.Contains(decision.OldHoldId))
            {
                continue;
            }

            var paired = decision.Kind == CarryKind.Removed ? null : decision.NewHoldId;
            db.WallUpdateHoldDecisions.Add(new WallUpdateHoldDecision
            {
                SessionId = session.Id,
                Kind = WallUpdateHoldDecisionKind.Carry,
                HoldId = decision.OldHoldId,
                PairedHoldId = paired is { } p && live.Contains(p) ? p : null,
                CarryKind = decision.Kind,
            });
        }

        AddNewCentreRows(db, session.Id, acceptedNewCentreHoldIds, live, discarded: false);
        AddNewCentreRows(db, session.Id, removedNewCentreHoldIds, live, discarded: true);

        WallUpdateSessions.Touch(session, user.Id);
        await db.SaveChangesAsync();

        logger.LogDebug(
            "Wall update session {SessionId} carryover saved by {UserId}: {Carry} verdicts, {Kept} kept, {Dropped} discarded",
            session.Id, user.Id, carryover.Count, acceptedNewCentreHoldIds.Count, removedNewCentreHoldIds.Count);
    }

    private static void AddNewCentreRows(
        BlocwerkDbContext db, Guid sessionId, IReadOnlyList<Guid> holdIds, IReadOnlySet<Guid> live, bool discarded)
    {
        foreach (var holdId in holdIds.Distinct())
        {
            if (!live.Contains(holdId))
            {
                continue;
            }

            db.WallUpdateHoldDecisions.Add(new WallUpdateHoldDecision
            {
                SessionId = sessionId,
                Kind = WallUpdateHoldDecisionKind.NewCentreHold,
                HoldId = holdId,
                Discarded = discarded,
            });
        }
    }

    private static List<Guid> CollectHoldIds(
        IReadOnlyList<CarryoverDecision> carryover,
        IReadOnlyList<Guid> accepted,
        IReadOnlyList<Guid> removed)
    {
        var ids = new List<Guid>();
        foreach (var decision in carryover)
        {
            ids.Add(decision.OldHoldId);
            if (decision.NewHoldId is { } newId)
            {
                ids.Add(newId);
            }
        }

        ids.AddRange(accepted);
        ids.AddRange(removed);
        return ids;
    }

    /// <summary>The subset of <paramref name="candidates"/> that still exists on the wall.</summary>
    private static async Task<HashSet<Guid>> LoadLiveHoldIdsAsync(
        BlocwerkDbContext db, Guid wallId, List<Guid> candidates)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var distinct = candidates.Distinct().ToList();
        var found = await db.Holds
            .Where(h => h.WallId == wallId && distinct.Contains(h.Id))
            .Select(h => h.Id)
            .ToListAsync();
        return found.ToHashSet();
    }
}
