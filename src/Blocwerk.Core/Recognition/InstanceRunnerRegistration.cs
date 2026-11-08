// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Recognition;

/// <summary>
/// At start, makes the instance runner's row (runner protocol 1.2, §1.5) match the deployment: one
/// <see cref="RunnerScope.Instance"/> runner without owner whose key hash is <see cref="RecognitionQueueOptions.InstanceKeyHash"/>.
/// Without a key (or one of a refused length, <see cref="RecognitionQueueOptions.InstanceKeyRefused"/>, logged as a warning)
/// the row is revoked, so an old key stops working. Only the hash ever reaches the database or the log.
/// </summary>
public sealed class InstanceRunnerRegistration(
    RootDbContextFactory dbContextFactory, RecognitionQueueOptions options, ILogger<InstanceRunnerRegistration> logger) : IHostedService
{
    /// <summary>The instance runner's name (it is never shown in the runners UI).</summary>
    public const string RunnerName = "Instance recognition runner";

    public Task StartAsync(CancellationToken cancellationToken) => ApplyAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Creates, re-keys or revokes the instance runner's row.</summary>
    public async Task ApplyAsync(CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var row = await db.GpuRunners.FirstOrDefaultAsync(r => r.Scope == RunnerScope.Instance, ct);
        if (options.InstanceKeyHash is not { } hash)
        {
            if (row is { RevokedAt: null })
            {
                row.RevokedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
            }

            if (options.InstanceKeyRefused is { } why)
            {
                logger.LogWarning("RECOGNITION__INSTANCEKEY is refused ({Why}): no instance recognition runner", why);
            }
            else
            {
                logger.LogInformation("No RECOGNITION__INSTANCEKEY: no instance recognition runner");
            }

            return;
        }

        if (row is null)
        {
            row = new GpuRunner { Name = RunnerName, Scope = RunnerScope.Instance, KeyHash = hash, KeyPrefix = "instance" };
            db.GpuRunners.Add(row);
        }

        row.KeyHash = hash;
        row.RevokedAt = null;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Instance recognition runner {RunnerId} is ready for its key", row.Id);
    }
}
