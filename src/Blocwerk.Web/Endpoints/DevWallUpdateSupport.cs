using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Web.Endpoints;

/// <summary>The impersonating big-update service plus the DB factory, for one dev request.</summary>
internal sealed record DevRunContext(
    WallBigUpdateService Service,
    Guid OwnerId,
    IDbContextFactory<BlocwerkDbContext> Factory);

/// <summary>
/// Shared plumbing for <see cref="DevWallUpdateEndpoints"/>: it constructs the REAL
/// <see cref="WallBigUpdateService"/> with an owner-impersonating current-user service (so the
/// no-auth dev routes still pass the wall-admin guard), builds the default carry-all promote
/// confirmation from a run's proposals, and shapes the JSON responses out of the DB.
/// </summary>
internal static class DevWallUpdateSupport
{
    /// <summary>
    /// Resolves the wall's owner and hands back the real big-update service wired to impersonate it.
    /// Null when the wall (or its owner) does not exist. CurrentUserId is left as Guid.Empty for the
    /// lookup, which disables the wall membership query filter.
    /// </summary>
    public static async Task<DevRunContext?> BuildServiceAsync(HttpContext http, Guid wallId)
    {
        var services = http.RequestServices;
        var factory = services.GetRequiredService<IDbContextFactory<BlocwerkDbContext>>();

        await using var db = await factory.CreateDbContextAsync();
        db.CurrentUserId = Guid.Empty;
        var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId);
        if (wall is null)
        {
            return null;
        }

        var owner = await db.Users.FirstOrDefaultAsync(u => u.Id == wall.OwnerId);
        if (owner is null)
        {
            return null;
        }

        var service = new WallBigUpdateService(
            factory,
            new DevOwnerCurrentUserService(owner),
            services.GetRequiredService<IHoldDetectionService>(),
            services.GetRequiredService<IHoldOverlapMatcher>(),
            services.GetRequiredService<ILogger<WallBigUpdateService>>());
        return new DevRunContext(service, owner.Id, factory);
    }

    /// <summary>
    /// The default promote decision: carry every proposed old→new mapping in place, accept every
    /// genuinely-new centre hold, and confirm every neighbour overlap proposal as a link. Old holds
    /// with no proposal are reconciled as default-carried inside the service. This is the "confirm
    /// with the matcher's suggestions unchanged" baseline the iteration measures against.
    /// <para>
    /// Accepted-new centre holds are derived from the DB exactly as the real UI does
    /// (<c>CarryoverReview.Continue</c>): ALL staged centre detections minus the ones a carryover
    /// proposal already consumes as a twin — NOT <see cref="BigUpdateSession.NewCenterHoldIds"/>,
    /// which is EMPTY when the matcher failed (status Failed/Unavailable) and would otherwise make
    /// <c>ReconcileNewCentreHolds</c> hard-delete every staged detection.
    /// </para>
    /// </summary>
    public static async Task<BigUpdateConfirmation> BuildCarryAllConfirmationAsync(
        IDbContextFactory<BlocwerkDbContext> factory, BigUpdateSession session)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentUserId = Guid.Empty;

        var wall = await db.Walls.FirstAsync(w => w.Id == session.WallId);
        var stagedGen = wall.CurrentGeneration + 1;

        var stagedCentreIds = await db.Holds
            .Where(h => h.WallPanelId == session.CenterPanelId && h.Generation == stagedGen)
            .Select(h => h.Id)
            .ToListAsync();

        // Twins already consumed by a carryover proposal must not also be kept as standalone new holds.
        var consumed = session.Carryover.Select(p => p.NewHoldId).ToHashSet();
        var accepted = stagedCentreIds.Where(id => !consumed.Contains(id)).ToList();

        var carryover = session.Carryover
            .Select(p => new CarryoverDecision(p.OldHoldId, CarryKind.Carried, p.NewHoldId))
            .ToList();

        var neighbours = session.Neighbours
            .Select(n => new NeighbourLinkSet(
                n.PanelId,
                n.Proposals.Select(pr => new ConfirmedLink(pr.HoldAId, pr.HoldBId, pr.Moved)).ToList(),
                []))
            .ToList();

        return new BigUpdateConfirmation(
            carryover, accepted, [], neighbours, session.CarriedWarpPositions, session.CarriedWarpShapes);
    }

    /// <summary>
    /// THE headline KPI of the whole wall-generations exercise, measured from the in-flight session's
    /// <see cref="BigUpdateSession.RemovedCandidateHoldIds"/> (the UnmatchedLeft old holds the matcher
    /// could not carry to a new position — so they stay at stale coords and a human must fix them):
    /// how many DISTINCT active boulders (<c>!IsArchived &amp;&amp; !IsHistoric</c>) reference any of
    /// those old holds. This is the PREDICTIVE score, so callers must compute it PRE-promote.
    /// </summary>
    public static async Task<RevisionForecast> ComputeRevisionForecastAsync(
        BlocwerkDbContext db, IReadOnlyList<Guid> removedCandidateHoldIds)
    {
        var affected = db.BoulderHolds
            .Where(bh => removedCandidateHoldIds.Contains(bh.HoldId)
                && !bh.Boulder.IsArchived && !bh.Boulder.IsHistoric);

        var count = await affected
            .Select(bh => bh.BoulderId)
            .Distinct()
            .CountAsync();

        var rows = await affected
            .Select(bh => new { bh.BoulderId, bh.Boulder.Name })
            .Distinct()
            .Take(50)
            .ToListAsync();

        return new RevisionForecast(
            count,
            removedCandidateHoldIds.Count,
            rows.Select(r => r.BoulderId).ToList(),
            rows.Select(r => r.Name).ToList());
    }

    public static async Task<object> BuildRunResponseAsync(
        IDbContextFactory<BlocwerkDbContext> factory, Guid wallId, BigUpdateSession session)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentUserId = Guid.Empty;

        var wall = await db.Walls.FirstAsync(w => w.Id == wallId);
        var currentGen = wall.CurrentGeneration;
        var stagedGen = currentGen + 1;

        var oldHoldsCount = await db.Holds.CountAsync(h => h.WallId == wallId && h.Generation == currentGen);

        var revision = await ComputeRevisionForecastAsync(db, session.RemovedCandidateHoldIds);

        var ids = session.Carryover
            .SelectMany(p => new[] { p.OldHoldId, p.NewHoldId })
            .Distinct()
            .ToList();
        var holds = await db.Holds.Where(h => ids.Contains(h.Id)).ToDictionaryAsync(h => h.Id);

        var proposals = session.Carryover.Select(p =>
        {
            holds.TryGetValue(p.OldHoldId, out var oldHold);
            holds.TryGetValue(p.NewHoldId, out var newHold);
            return new
            {
                oldHoldId = p.OldHoldId,
                newHoldId = p.NewHoldId,
                oldX = oldHold?.X,
                oldY = oldHold?.Y,
                oldRadius = oldHold?.Radius,
                newX = newHold?.X,
                newY = newHold?.Y,
                newRadius = newHold?.Radius,
                residualPx = p.ResidualPx,
                confidence = p.Confidence,
            };
        }).ToList();

        return new
        {
            stagedGeneration = stagedGen,
            autoMatchStatus = session.AutoMatchStatus.ToString(),
            autoMatchMessage = session.AutoMatchMessage,
            counts = new
            {
                oldHolds = oldHoldsCount,

                // Under carry-all, removal candidates are NOT removed — they are carried blind (cloned
                // forward at their old coords), so they count as carried and nothing is removed.
                carried = session.Carryover.Count + session.RemovedCandidateHoldIds.Count,
                changed = 0,
                removed = 0,
                removalCandidates = session.RemovedCandidateHoldIds.Count,
                newCentre = session.NewCenterHoldIds.Count,
            },

            // THE primary KPI: active boulders left on stale coords by the matcher's misses.
            bouldersNeedingRevision = revision.BouldersNeedingRevision,
            unmatchedLeftHolds = revision.UnmatchedLeftHolds,
            bouldersNeedingRevisionIds = revision.BoulderIds,
            bouldersNeedingRevisionNames = revision.BoulderNames,
            proposals,
        };
    }

    public static async Task<object> BuildPromoteResponseAsync(
        IDbContextFactory<BlocwerkDbContext> factory, Guid wallId, Guid centerPanelId, RevisionForecast revision)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentUserId = Guid.Empty;

        var wall = await db.Walls.FirstAsync(w => w.Id == wallId);
        var newGen = wall.CurrentGeneration;

        var bouldersNeedingReview = await db.Boulders
            .CountAsync(b => b.WallId == wallId && !b.IsArchived && !b.IsHistoric && b.NeedsReview);

        var holdsChanged = await db.HoldGenerationLinks
            .CountAsync(l => l.WallId == wallId && l.ToGeneration == newGen && l.Kind == HoldGenerationLinkKind.Changed);
        var holdsCarried = await db.HoldGenerationLinks
            .CountAsync(l => l.WallId == wallId && l.ToGeneration == newGen && l.Kind == HoldGenerationLinkKind.Same);

        // Genuinely-new centre holds: rows at the new generation on the centre panel that no
        // generation link carried forward (a carried/changed hold is always a link's successor).
        var linkTargets = await db.HoldGenerationLinks
            .Where(l => l.WallId == wallId && l.ToGeneration == newGen)
            .Select(l => l.NewHoldId)
            .ToListAsync();
        var holdsNew = await db.Holds
            .CountAsync(h => h.WallPanelId == centerPanelId && h.Generation == newGen && !linkTargets.Contains(h.Id));

        return new
        {
            newGeneration = newGen,
            bouldersNeedingReview,
            holdsChanged,
            holdsCarried,
            holdsNew,

            // THE primary KPI, measured PRE-promote from the session's unmatched-left old holds.
            bouldersNeedingRevision = revision.BouldersNeedingRevision,
            unmatchedLeftHolds = revision.UnmatchedLeftHolds,
            bouldersNeedingRevisionIds = revision.BoulderIds,
            bouldersNeedingRevisionNames = revision.BoulderNames,
        };
    }
}

/// <summary>
/// The predictive revision KPI for one in-flight session: how many distinct active boulders reference
/// an old hold the matcher failed to carry, plus those boulders' ids/names (capped) for inspection.
/// </summary>
internal sealed record RevisionForecast(
    int BouldersNeedingRevision,
    int UnmatchedLeftHolds,
    IReadOnlyList<Guid> BoulderIds,
    IReadOnlyList<string> BoulderNames);
