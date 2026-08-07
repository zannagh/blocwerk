using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Helpers;
using Blocwerk.Core.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

public interface ITrainingService
{
    Task<HangboardSession> SaveHangboardSessionAsync(int edgeSizeMm, double additionalWeightKg, TimeSpan duration, int sets, string? notes = null);

    Task<PullupSession> SavePullupSessionAsync(int repetitions, double additionalWeightKg, int sets, string? notes = null);

    Task DeleteHangboardSessionAsync(Guid sessionId);

    Task DeletePullupSessionAsync(Guid sessionId);
}

public class TrainingService : ITrainingService
{
    private readonly IDbContextFactory<BlocwerkDbContext> _dbContextFactory;
    private readonly ICurrentUserService _currentUserService;
    private readonly ILogger<TrainingService> _logger;

    public TrainingService(IDbContextFactory<BlocwerkDbContext> dbContextFactory, ICurrentUserService currentUserService, ILogger<TrainingService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _currentUserService = currentUserService;
        _logger = logger;
    }

    public async Task<HangboardSession> SaveHangboardSessionAsync(int edgeSizeMm, double additionalWeightKg, TimeSpan duration, int sets, string? notes = null)
    {
        using var op = BlocwerkMetrics.TimeOperation("Training.SaveHangboard");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();

            var session = new HangboardSession
            {
                UserId = user.Id,
                EdgeSizeMm = edgeSizeMm,
                AdditionalWeightKg = additionalWeightKg,
                Duration = duration,
                Sets = sets,
                Notes = notes,
            };

            session.ActivityId = await ActivityGrouping.ResolveActivityIdAsync(db, user.Id, session.Timestamp, null);

            db.HangboardSessions.Add(session);
            await db.SaveChangesAsync();
            _logger.LogInformation("Hangboard session {SessionId} saved for {UserId}", session.Id, user.Id);
            return session;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<PullupSession> SavePullupSessionAsync(int repetitions, double additionalWeightKg, int sets, string? notes = null)
    {
        using var op = BlocwerkMetrics.TimeOperation("Training.SavePullup");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();

            var session = new PullupSession
            {
                UserId = user.Id,
                Repetitions = repetitions,
                AdditionalWeightKg = additionalWeightKg,
                Sets = sets,
                Notes = notes,
            };

            session.ActivityId = await ActivityGrouping.ResolveActivityIdAsync(db, user.Id, session.Timestamp, null);

            db.PullupSessions.Add(session);
            await db.SaveChangesAsync();
            _logger.LogInformation("Pullup session {SessionId} saved for {UserId}", session.Id, user.Id);
            return session;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task DeleteHangboardSessionAsync(Guid sessionId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Training.DeleteHangboard");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();

            var session = await db.HangboardSessions
                .FirstOrDefaultAsync(h => h.Id == sessionId && h.UserId == user.Id);

            if (session is null)
            {
                _logger.LogWarning("Hangboard session {SessionId} not found while deleting for {UserId}", sessionId, user.Id);
                throw new InvalidOperationException("Session not found");
            }

            db.HangboardSessions.Remove(session);
            await db.SaveChangesAsync();
            _logger.LogInformation("Hangboard session {SessionId} deleted for {UserId}", sessionId, user.Id);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task DeletePullupSessionAsync(Guid sessionId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Training.DeletePullup");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();

            var session = await db.PullupSessions
                .FirstOrDefaultAsync(p => p.Id == sessionId && p.UserId == user.Id);

            if (session is null)
            {
                _logger.LogWarning("Pullup session {SessionId} not found while deleting for {UserId}", sessionId, user.Id);
                throw new InvalidOperationException("Session not found");
            }

            db.PullupSessions.Remove(session);
            await db.SaveChangesAsync();
            _logger.LogInformation("Pullup session {SessionId} deleted for {UserId}", sessionId, user.Id);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }
}
