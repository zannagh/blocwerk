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
            .Select(d => new CarryoverDecision(d.HoldId, d.CarryKind, d.PairedHoldId, d.Confirmed))
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
            (row, userId) => CarryConfirmationPolicy.Apply(
                row,
                decision.Kind,
                decision.Kind == CarryKind.Removed ? null : decision.NewHoldId,
                decision.Confirmed,
                userId,
                DateTimeOffset.UtcNow));
    }

    /// <inheritdoc/>
    public async Task ClearCarryConfirmationAsync(Guid wallId, Guid oldHoldId)
    {
        await UpsertHoldDecisionAsync(
            wallId,
            WallUpdateHoldDecisionKind.Carry,
            oldHoldId,
            (row, _) => CarryConfirmationPolicy.Clear(row));
    }

    /// <inheritdoc/>
    public async Task SaveNewCentreHoldDecisionAsync(Guid wallId, Guid stagedHoldId, bool discarded)
    {
        await UpsertHoldDecisionAsync(
            wallId,
            WallUpdateHoldDecisionKind.NewCentreHold,
            stagedHoldId,
            (row, _) => row.Discarded = discarded);
    }

    /// <summary>
    /// Inserts or updates the one row for (session, kind, subject hold) and commits. The unique index on
    /// that triple is what makes this an upsert rather than an append.
    /// </summary>
    private async Task UpsertHoldDecisionAsync(
        Guid wallId,
        WallUpdateHoldDecisionKind kind,
        Guid holdId,
        Action<WallUpdateHoldDecision, Guid> apply)
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

        apply(row, user.Id);
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
        AddCarryRows(db, session.Id, carryover, live, CarryRowsByHold(existing));
        AddNewCentreRows(db, session.Id, acceptedNewCentreHoldIds, live, discarded: false);
        AddNewCentreRows(db, session.Id, removedNewCentreHoldIds, live, discarded: true);

        WallUpdateSessions.Touch(session, user.Id);
        await db.SaveChangesAsync();

        logger.LogDebug(
            "Wall update session {SessionId} carryover saved by {UserId}: {Carry} verdicts, {Kept} kept, {Dropped} discarded",
            session.Id, user.Id, carryover.Count, acceptedNewCentreHoldIds.Count, removedNewCentreHoldIds.Count);
    }

    /// <summary>The carry rows being replaced, by subject hold, so their confirmation can be carried over.</summary>
    private static Dictionary<Guid, WallUpdateHoldDecision> CarryRowsByHold(List<WallUpdateHoldDecision> existing)
    {
        return existing
            .Where(d => d.Kind == WallUpdateHoldDecisionKind.Carry)
            .GroupBy(d => d.HoldId)
            .ToDictionary(g => g.Key, g => g.First());
    }

    /// <summary>
    /// Re-adds the carryover half. The rows are new, but each one starts from the row it replaces so a
    /// human confirmation is not silently dropped by a bulk replay that re-states the same verdict —
    /// <see cref="CarryConfirmationPolicy"/> then decides whether it survives or is cleared.
    /// <para>
    /// This path NEVER confirms. The decisions it replays came out of a read (the review seeds itself
    /// from <see cref="GetDecisionsAsync"/>, whose <c>Confirmed</c> is the persisted FACT), so a true
    /// arriving here is a stale echo of what the row already said, not somebody's intent — and echoing
    /// it back would re-confirm a sign-off another admin cleared in the meantime and stamp it with the
    /// name of whoever happened to press Continue. Every deliberate confirmation is written one hold at
    /// a time through <see cref="SaveCarryDecisionAsync"/>; nothing relies on this save to register one.
    /// </para>
    /// </summary>
    private static void AddCarryRows(
        BlocwerkDbContext db,
        Guid sessionId,
        IReadOnlyList<CarryoverDecision> carryover,
        IReadOnlySet<Guid> live,
        IReadOnlyDictionary<Guid, WallUpdateHoldDecision> previous)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var decision in carryover)
        {
            if (!live.Contains(decision.OldHoldId))
            {
                continue;
            }

            var row = new WallUpdateHoldDecision
            {
                SessionId = sessionId,
                Kind = WallUpdateHoldDecisionKind.Carry,
                HoldId = decision.OldHoldId,
                UpdatedAt = now,
            };
            if (previous.TryGetValue(decision.OldHoldId, out var prior))
            {
                CarryConfirmationPolicy.CopyFrom(row, prior);
            }

            // confirmed: false is the whole point of this path (see the remarks) — a verdict that MOVED
            // still drops the stored sign-off, because that sign-off was about the verdict that went
            // away. userId is unread while confirmed is false: the policy records who only when it
            // confirms.
            var paired = decision.Kind == CarryKind.Removed ? null : decision.NewHoldId;
            CarryConfirmationPolicy.Apply(
                row,
                decision.Kind,
                paired is { } p && live.Contains(p) ? p : null,
                confirmed: false,
                userId: Guid.Empty,
                now);
            db.WallUpdateHoldDecisions.Add(row);
        }
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
