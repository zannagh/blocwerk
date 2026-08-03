using Blocwerk.Core.Configuration;
using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// One-time Development seed: when the isolated dev database is empty and a read-only
/// <see cref="BlocwerkSettings.DevImport"/> source (e.g. production) is configured, clone the
/// source's data into the dev database so local testing has realistic data while never writing to
/// production. Idempotent — skips once the dev DB has any wall.
/// </summary>
public static class DevDataImporter
{
    public static async Task ImportIfNeededAsync(
        IDbContextFactory<BlocwerkDbContext> targetFactory,
        BlocwerkSettings settings,
        ILogger logger)
    {
        if (settings.DevImport is null)
        {
            logger.LogInformation(
                "Dev import: no DevImport source configured — using the dev database as-is.");
            return;
        }

        await using var target = await targetFactory.CreateDbContextAsync();
        target.CurrentUserId = Guid.Empty;

        if (await target.Walls.AnyAsync())
        {
            logger.LogInformation("Dev import: dev database already has data — skipping clone.");
            return;
        }

        var source = new BlocwerkDbContext(
            new DbContextOptionsBuilder<BlocwerkDbContext>()
                .UseNpgsql(settings.DevImport.ConnectionString)
                .Options)
        {
            // Guid.Empty bypasses the Wall query filter so every wall is read.
            CurrentUserId = Guid.Empty,
        };

        await using (source)
        {
            logger.LogInformation(
                "Dev import: cloning from {Host}:{Port}/{Db} into the dev database…",
                settings.DevImport.Host, settings.DevImport.Port, settings.DevImport.Database);

            // Copy each table flat (no Includes) in FK dependency order. Reading flat + AsNoTracking
            // avoids double-inserting shared rows; existing GUID / composite keys and User.Identifier
            // are preserved, so ownership and the identity you log in as line up with the clone.
            await CopyAsync(source.Users, target, target.Users, logger);
            await CopyAsync(source.Walls, target, target.Walls, logger);
            await CopyAsync(source.WallMembers, target, target.WallMembers, logger);
            await CopyAsync(source.WallSegments, target, target.WallSegments, logger);
            await CopyAsync(source.Holds, target, target.Holds, logger);
            await CopyAsync(source.Boulders, target, target.Boulders, logger);
            await CopyAsync(source.BoulderHolds, target, target.BoulderHolds, logger);
            await CopyAsync(source.Attempts, target, target.Attempts, logger);
            await CopyAsync(source.BoulderComments, target, target.BoulderComments, logger);
            await CopyAsync(source.GradeProposals, target, target.GradeProposals, logger);
            await CopyAsync(source.BoulderRatings, target, target.BoulderRatings, logger);
            await CopyAsync(source.BoulderFavorites, target, target.BoulderFavorites, logger);
            await CopyAsync(source.WallResets, target, target.WallResets, logger);
            await CopyAsync(source.ActivityLog, target, target.ActivityLog, logger);
            await CopyAsync(source.RefreshTokens, target, target.RefreshTokens, logger);
            await CopyAsync(source.HangboardSessions, target, target.HangboardSessions, logger);
            await CopyAsync(source.PullupSessions, target, target.PullupSessions, logger);
            await CopyAsync(source.ClimbingSessions, target, target.ClimbingSessions, logger);

            logger.LogInformation("Dev import: clone complete.");
        }
    }

    private static async Task CopyAsync<T>(
        IQueryable<T> sourceSet,
        BlocwerkDbContext target,
        DbSet<T> targetSet,
        ILogger logger)
        where T : class
    {
        var rows = await sourceSet.AsNoTracking().ToListAsync();
        if (rows.Count == 0)
        {
            return;
        }

        await targetSet.AddRangeAsync(rows);
        await target.SaveChangesAsync();
        logger.LogInformation("Dev import: copied {Count} {Entity}.", rows.Count, typeof(T).Name);
    }
}
