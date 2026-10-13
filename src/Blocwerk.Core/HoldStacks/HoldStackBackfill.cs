// <copyright file="HoldStackBackfill.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.HoldStacks;

/// <summary>
/// The one-time startup step for walls from before holds could sit on holds: every live hold that lies inside a
/// clearly larger one (<see cref="HoldStacking"/>) is stacked on it, so nobody has to draw the stacks of an existing
/// wall. <see cref="Entities.Wall.HoldStacksBackfilledAt"/> makes it run once per wall: a hold a person takes off its
/// base afterwards stays off. Virtual holds and holds the last update did not find are left alone.
/// </summary>
public static class HoldStackBackfill
{
    /// <summary>The journal batch label of a backfill.</summary>
    public const string BatchLabel = "hold-stack-backfill";

    /// <summary>Backfills every wall that needs it; a wall that fails is retried on the next start.</summary>
    /// <param name="factory">Context factory (the journalling one in production).</param>
    /// <param name="journal">Opens the per-wall batch.</param>
    /// <param name="logger">Logger.</param>
    /// <returns>A task.</returns>
    public static async Task RunIfNeededAsync(IDbContextFactory<BlocwerkDbContext> factory, IChangeJournal? journal, ILogger logger)
    {
        await using var list = await factory.CreateDbContextAsync();
        var wallIds = await list.Walls.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.HoldStacksBackfilledAt == null)
            .Select(w => w.Id)
            .ToListAsync();
        foreach (var wallId in wallIds)
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync();
                using var batch = journal?.BeginBatch(BatchLabel, ChangeJournalScopeKind.Wall, wallId);
                var (stacked, holds) = await BackfillWallAsync(db, wallId, CancellationToken.None);
                logger.LogInformation("Hold-stack backfill on wall {WallId}: {Stacked} of {Holds} live holds stacked on another hold", wallId, stacked, holds);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Hold-stack backfill failed for wall {WallId}; it is retried on the next start.", wallId);
            }
        }
    }

    /// <summary>Stacks the wall's live holds that have no base yet, marks the wall as done, and saves.</summary>
    /// <param name="db">The context.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many holds got a base, of how many live holds.</returns>
    internal static async Task<(int Stacked, int Holds)> BackfillWallAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        var wall = await db.Walls.IgnoreQueryFilters().FirstAsync(w => w.Id == wallId, ct);
        var live = await (await LiveWallHolds.QueryAsync(db, wallId, ct)).ToListAsync(ct);
        var stacked = wall.HoldStacksBackfilledAt is null ? await HoldStacker.StackAllAsync(db, live, ct) : 0;
        wall.HoldStacksBackfilledAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return (stacked, live.Count);
    }
}
