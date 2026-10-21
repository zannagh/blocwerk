// <copyright file="WallEnhancementResumer.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Enhancement;

/// <summary>
/// Picks up enhancement runs a restart interrupted. The run state is persisted step by step and a resumed run skips
/// every finished step, so on startup each run still marked Running is simply launched again — nobody has to notice
/// "interrupted" and press anything.
/// </summary>
public sealed class WallEnhancementResumer(
    IDbContextFactory<BlocwerkDbContext> dbContextFactory, WallEnhancementRunner runner, ILogger<WallEnhancementResumer> logger)
    : BackgroundService
{
    /// <summary>Launches every interrupted run.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many were resumed.</returns>
    public async Task<int> ResumeInterruptedAsync(CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;
        var ids = await db.WallEnhancementRuns.AsNoTracking()
            .Where(r => r.Status == WallEnhancementStatus.Running)
            .Select(r => r.Id)
            .ToListAsync(ct);
        foreach (var id in ids.Where(id => !runner.IsRunning(id)))
        {
            logger.LogInformation("Resuming the interrupted wall enhancement {RunId}", id);
            runner.Launch(id);
        }

        return ids.Count;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ResumeInterruptedAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never block startup: the run stays Running and resumes on the next start.
            logger.LogWarning(ex, "Could not resume interrupted wall enhancements");
        }
    }
}
