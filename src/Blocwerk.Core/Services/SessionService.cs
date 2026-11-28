using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

public interface ISessionService
{
    /// <summary>
    /// Starts a session on the given wall, ending any session the user already had open. The wall
    /// must be one the user can see, or this throws.
    /// </summary>
    Task<ClimbingSession> StartSessionAsync(Guid wallId, Guid? clientRequestId = null);

    /// <summary>
    /// The user's live session, or null when none is open. A session whose day has passed is
    /// closed here rather than kept alive, so a forgotten session never lingers past its day.
    /// </summary>
    Task<ClimbingSession?> GetActiveSessionAsync();

    /// <summary>Ends the user's live session, if any.</summary>
    Task EndSessionAsync();
}

public class SessionService : ISessionService
{
    /// <summary>The refusal of a <c>clientRequestId</c> the caller already used to start a session on another wall.</summary>
    public const string ReusedRequestIdMessage = "This clientRequestId was already used to start a session on another wall.";

    private readonly IDbContextFactory<BlocwerkDbContext> _dbContextFactory;
    private readonly ICurrentUserService _currentUserService;
    private readonly ILogger<SessionService> _logger;
    private readonly IPushNotificationService? _pushNotificationService;

    public SessionService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ICurrentUserService currentUserService,
        ILogger<SessionService> logger,
        IPushNotificationService? pushNotificationService = null)
    {
        _dbContextFactory = dbContextFactory;
        _currentUserService = currentUserService;
        _logger = logger;
        _pushNotificationService = pushNotificationService;
    }

    public async Task<ClimbingSession> StartSessionAsync(Guid wallId, Guid? clientRequestId = null)
    {
        ClientRequestIds.EnsureValid(clientRequestId);
        using var op = BlocwerkMetrics.TimeOperation("Session.Start", wallId);
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            // The query filter on Wall only returns walls the user is a member of, so a wall the user
            // cannot see comes back null and is rejected.
            var wallExists = await db.Walls.AnyAsync(w => w.Id == wallId);
            if (!wallExists)
            {
                _logger.LogWarning("Cannot start session: wall {WallId} not found for {UserId}", wallId, user.Id);
                throw new InvalidOperationException("Wall not found");
            }

            if (await ReplayedStartAsync(db, user.Id, clientRequestId) is { } replayed)
            {
                // The same key for another wall is a client bug, not a replay.
                return replayed.WallId == wallId
                    ? replayed
                    : throw new ArgumentOutOfRangeException(nameof(clientRequestId), ReusedRequestIdMessage);
            }

            await CloseOpenSessions(db, user.Id);

            var session = new ClimbingSession
            {
                UserId = user.Id,
                WallId = wallId,
                ClientRequestId = clientRequestId,
            };

            db.ClimbingSessions.Add(session);
            await db.SaveChangesAsync();

            await AnnounceStartAsync(session);
            session.Wall = await db.Walls.Where(w => w.Id == wallId).WithoutPhotos().FirstAsync();
            return session;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<ClimbingSession?> GetActiveSessionAsync()
    {
        using var op = BlocwerkMetrics.TimeOperation("Session.GetActive");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            // Wall rows without their photo blobs, attached first so the session's Wall is fixed up by EF.
            await db.AttachVisibleWallsWithoutPhotosAsync();
            var session = await db.ClimbingSessions
                .Where(s => s.UserId == user.Id && s.EndedAt == null)
                .OrderByDescending(s => s.StartedAt)
                .FirstOrDefaultAsync();

            if (session == null)
            {
                return null;
            }

            // Auto-close once the day has passed: a session is only live on the calendar day it began.
            if (session.StartedAt.UtcDateTime.Date != DateTimeOffset.UtcNow.UtcDateTime.Date)
            {
                session.EndedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
                _logger.LogInformation("Auto-closed stale session {SessionId} for {UserId}", session.Id, user.Id);
                return null;
            }

            return session;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task EndSessionAsync()
    {
        using var op = BlocwerkMetrics.TimeOperation("Session.End");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            await CloseOpenSessions(db, user.Id);
            await db.SaveChangesAsync();

            _logger.LogInformation("Session ended for {UserId}", user.Id);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    // After the commit: count it, log it and tell the wall's other members. The push is internally guarded, so a push
    // failure never surfaces here and can never break or block starting the session.
    private async Task AnnounceStartAsync(ClimbingSession session)
    {
        BlocwerkMetrics.RecordSessionStarted(session.WallId);
        _logger.LogInformation("Session {SessionId} started on wall {WallId} by {UserId}", session.Id, session.WallId, session.UserId);
        if (_pushNotificationService is not null)
        {
            await _pushNotificationService.NotifySessionStartedAsync(session.WallId, session.UserId);
        }
    }

    // A replay of the caller's own earlier start returns that session as it is now, and closes nothing: a retried start
    // must not end the session it started.
    private static async Task<ClimbingSession?> ReplayedStartAsync(BlocwerkDbContext db, Guid userId, Guid? clientRequestId)
    {
        if (clientRequestId is not { } requestId
            || await db.ClimbingSessions.FirstOrDefaultAsync(c => c.UserId == userId && c.ClientRequestId == requestId) is not { } replayed)
        {
            return null;
        }

        replayed.Wall = await db.Walls.Where(w => w.Id == replayed.WallId).WithoutPhotos().FirstAsync();
        return replayed;
    }

    private static async Task CloseOpenSessions(BlocwerkDbContext db, Guid userId)
    {
        var open = await db.ClimbingSessions
            .Where(s => s.UserId == userId && s.EndedAt == null)
            .ToListAsync();

        foreach (var session in open)
        {
            session.EndedAt = DateTimeOffset.UtcNow;
        }
    }
}
