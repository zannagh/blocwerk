// <copyright file="WallShapeRecognitionResumer.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// Picks up shape-recognition runs a restart interrupted. The run state is persisted on the session and a
/// resumed run skips every hold already outlined, so on startup each open session still marked Running is
/// simply launched again — nobody has to notice "interrupted" and press anything.
/// </summary>
public sealed class WallShapeRecognitionResumer : BackgroundService
{
    private readonly RootDbContextFactory factory;
    private readonly WallShapeRecognitionRunner runner;
    private readonly ILogger<WallShapeRecognitionResumer> logger;

    public WallShapeRecognitionResumer(
        RootDbContextFactory factory, WallShapeRecognitionRunner runner, ILogger<WallShapeRecognitionResumer> logger)
    {
        this.factory = factory;
        this.runner = runner;
        this.logger = logger;
    }

    /// <summary>Launches every interrupted run. Returns how many were resumed.</summary>
    public async Task<int> ResumeInterruptedAsync(CancellationToken ct)
    {
        if (!runner.Available)
        {
            return 0;
        }

        await using var db = factory.CreateDbContext();
        db.CurrentUserId = Guid.Empty;
        var ids = await db.WallUpdateSessions.AsNoTracking()
            .Where(s => s.Status == WallUpdateSessionStatus.Open && s.ShapeStatus == ShapeRecognitionStatus.Running)
            .Select(s => s.Id)
            .ToListAsync(ct);
        foreach (var id in ids.Where(id => !runner.IsRunning(id)))
        {
            logger.LogInformation("Resuming the interrupted shape recognition of wall update session {SessionId}", id);
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
            // Never block startup: an unresumed run still reports "interrupted" and resumes on the next start.
            logger.LogWarning(ex, "Could not resume interrupted shape recognition runs");
        }
    }
}
