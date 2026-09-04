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
    Task<ClimbingSession> StartSessionAsync(Guid wallId);

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

    public async Task<ClimbingSession> StartSessionAsync(Guid wallId)
    {
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

            await CloseOpenSessions(db, user.Id);

            var session = new ClimbingSession
            {
                UserId = user.Id,
                WallId = wallId,
            };

            db.ClimbingSessions.Add(session);
            await db.SaveChangesAsync();

            BlocwerkMetrics.RecordSessionStarted(wallId);

            _logger.LogInformation("Session {SessionId} started on wall {WallId} by {UserId}", session.Id, wallId, user.Id);

            // After the commit: tell the wall's other members. Internally guarded, so a push failure
            // never surfaces here and can never break or block starting the session.
            if (_pushNotificationService is not null)
            {
                await _pushNotificationService.NotifySessionStartedAsync(wallId, user.Id);
            }

            session.Wall = await db.Walls.FirstAsync(w => w.Id == wallId);
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

            var session = await db.ClimbingSessions
                .Include(s => s.Wall)
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
