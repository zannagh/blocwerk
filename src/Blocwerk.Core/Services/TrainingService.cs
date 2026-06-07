using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

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

    public TrainingService(IDbContextFactory<BlocwerkDbContext> dbContextFactory, ICurrentUserService currentUserService)
    {
        _dbContextFactory = dbContextFactory;
        _currentUserService = currentUserService;
    }

    public async Task<HangboardSession> SaveHangboardSessionAsync(int edgeSizeMm, double additionalWeightKg, TimeSpan duration, int sets, string? notes = null)
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

        db.HangboardSessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    public async Task<PullupSession> SavePullupSessionAsync(int repetitions, double additionalWeightKg, int sets, string? notes = null)
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

        db.PullupSessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    public async Task DeleteHangboardSessionAsync(Guid sessionId)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var session = await db.HangboardSessions
            .FirstOrDefaultAsync(h => h.Id == sessionId && h.UserId == user.Id)
            ?? throw new InvalidOperationException("Session not found");

        db.HangboardSessions.Remove(session);
        await db.SaveChangesAsync();
    }

    public async Task DeletePullupSessionAsync(Guid sessionId)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var session = await db.PullupSessions
            .FirstOrDefaultAsync(p => p.Id == sessionId && p.UserId == user.Id)
            ?? throw new InvalidOperationException("Session not found");

        db.PullupSessions.Remove(session);
        await db.SaveChangesAsync();
    }
}
