using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Data;

/// <summary>
/// The context-level operations on the <see cref="WallUpdateSession"/> header, shared by the two
/// services that own its lifecycle: <c>WallBigUpdateService</c> opens and closes sessions as part of
/// stage/promote/discard, and <c>WallUpdateSessionService</c> reads and updates them as the user
/// decides things. Neither saves here — the caller commits, so a session transition lands in the same
/// unit of work as the staged rows it describes.
/// </summary>
public static class WallUpdateSessions
{
    /// <summary>The wall's in-flight update session, or null when no update is open on it.</summary>
    public static Task<WallUpdateSession?> FindOpenAsync(
        BlocwerkDbContext db, Guid wallId, CancellationToken ct = default)
    {
        return db.WallUpdateSessions
            .FirstOrDefaultAsync(s => s.WallId == wallId && s.Status == WallUpdateSessionStatus.Open, ct);
    }

    /// <summary>
    /// Adds a fresh open session for the wall at the staged generation. The caller must have closed any
    /// prior open session first — at most one per wall is open, which is what makes "someone else is
    /// already updating this wall" detectable.
    /// </summary>
    public static WallUpdateSession Open(BlocwerkDbContext db, Guid wallId, int stagedGeneration, Guid userId)
    {
        var now = DateTimeOffset.UtcNow;
        var session = new WallUpdateSession
        {
            WallId = wallId,
            StagedGeneration = stagedGeneration,
            Status = WallUpdateSessionStatus.Open,
            Phase = WallUpdatePhase.Detected,
            NeighbourIndex = 0,
            CreatedAt = now,
            CreatedByUserId = userId,
            UpdatedAt = now,
            LastActiveByUserId = userId,
        };
        db.WallUpdateSessions.Add(session);
        return session;
    }

    /// <summary>
    /// Closes the wall's open session, if any, with the given terminal status. Its decision rows are
    /// left in place: they cascade away with the session only if the session itself is ever deleted,
    /// and keeping them makes a promoted session a readable record of what was decided.
    /// </summary>
    public static async Task<WallUpdateSession?> CloseOpenAsync(
        BlocwerkDbContext db,
        Guid wallId,
        WallUpdateSessionStatus status,
        Guid userId,
        CancellationToken ct = default)
    {
        var session = await FindOpenAsync(db, wallId, ct);
        if (session is null)
        {
            return null;
        }

        session.Status = status;
        session.ClosedAt = DateTimeOffset.UtcNow;
        Touch(session, userId);
        return session;
    }

    /// <summary>
    /// Refuses when <paramref name="expectedSessionId"/> is not the wall's currently-open session — the
    /// identity check that stops a stale browser from acting on somebody else's staging.
    /// <para>
    /// Null means "whatever is open", which is how a legacy staged update (staged before sessions
    /// existed, so there is no id to name) and the service-level tests still promote. Passing an id is
    /// what any caller that HAS one must do: the wizard always has one, and without the check its Apply
    /// button silently promoted the next admin's photos under the previous admin's decisions.
    /// </para>
    /// </summary>
    public static async Task EnsureCurrentAsync(
        BlocwerkDbContext db, Guid wallId, Guid? expectedSessionId, CancellationToken ct = default)
    {
        if (expectedSessionId is not { } expected)
        {
            return;
        }

        var open = await FindOpenAsync(db, wallId, ct);
        if (open is null || open.Id != expected)
        {
            throw new Services.WallUpdateSessionSupersededException(wallId, expected, open?.Id);
        }
    }

    /// <summary>Stamps who last wrote to the session and when. Every mutating path goes through this.</summary>
    public static void Touch(WallUpdateSession session, Guid userId)
    {
        session.UpdatedAt = DateTimeOffset.UtcNow;
        session.LastActiveByUserId = userId;
    }
}
