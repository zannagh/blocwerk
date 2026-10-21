// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Feed;

/// <summary>
/// The <see cref="IWallFeed"/> over a context factory. Each read takes the provider's ceiling first and then reads up to
/// it on the same connection, so no row is skipped (<see cref="IFeedHorizon"/>). A singleton: it holds no state beyond
/// "the epoch exists".
/// </summary>
public sealed class WallFeed : IWallFeed
{
    /// <summary>The largest page a read returns.</summary>
    public const int MaxLimit = 1000;

    // The feed's read connection only (E2): an idle open transaction would hold back the horizon for everyone, so this
    // session gives one up after a minute. Set per session, never on the role; the pool resets it when the connection returns.
    private const string IdleTimeoutSql = "SET idle_in_transaction_session_timeout = '60s'";

    private readonly Func<BlocwerkDbContext> createContext;
    private readonly ILogger<WallFeed> logger;
    private readonly SemaphoreSlim initializeGate = new(1, 1);
    private volatile bool initialized;

    public WallFeed(Func<BlocwerkDbContext> createContext, ILogger<WallFeed> logger)
    {
        this.createContext = createContext;
        this.logger = logger;
    }

    public async Task<Guid> InitializeAsync(CancellationToken cancellationToken = default)
    {
        await initializeGate.WaitAsync(cancellationToken);
        try
        {
            await using var db = createContext();
            var epoch = await FeedEpochStore.EnsureAsync(db, logger, cancellationToken);
            initialized = true;
            return epoch.Id;
        }
        finally
        {
            initializeGate.Release();
        }
    }

    public async Task<FeedCursor> CurrentAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await OpenReadContextAsync(cancellationToken);
        var epoch = await EpochAsync(db, cancellationToken);
        var horizon = FeedHorizons.For(db);
        var ceiling = await horizon.CeilingAsync(db, cancellationToken);
        return new FeedCursor(epoch.Id, await HighWaterAsync(db, horizon, ceiling, epoch, cancellationToken));
    }

    public async Task<FeedPage> ReadAsync(FeedFilter filter, FeedCursor after, int limit, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);
        await using var db = await OpenReadContextAsync(cancellationToken);
        var epoch = await EpochAsync(db, cancellationToken);
        var horizon = FeedHorizons.For(db);
        var ceiling = await horizon.CeilingAsync(db, cancellationToken);
        if (after.Epoch != epoch.Id || IsBelowFloor(after.Position, epoch))
        {
            return new FeedPage([], new FeedCursor(epoch.Id, await HighWaterAsync(db, horizon, ceiling, epoch, cancellationToken)), Resync: true);
        }

        var query = horizon.Between(db.WallChanges.AsNoTracking().Where(filter.Predicate), after.Position, ceiling);
        var rows = await horizon.InOrder(query).Take(limit).ToListAsync(cancellationToken);
        if (rows.Count == limit)
        {
            // A full page may have more below the ceiling.
            return new FeedPage(rows, new FeedCursor(epoch.Id, FeedPosition.Of(rows[^1])), Resync: false);
        }

        // A short page has seen everything up to the ceiling, but the cursor moves only to the high-water mark.
        var highWater = await HighWaterAsync(db, horizon, ceiling, epoch, cancellationToken);
        return new FeedPage(rows, new FeedCursor(epoch.Id, FeedPosition.Max(after.Position, highWater)), Resync: false);
    }

    public Task<long?> AppendAsync(BlocwerkDbContext db, WallChange row, CancellationToken cancellationToken = default) =>
        WallChangeInsert.ExecuteAsync(db, row, cancellationToken);

    // The newest stored position at or below the ceiling, or the retention floor if that is higher. No cursor is handed
    // out above it: the mark moves only when rows are written, so the startup check (high-water >= current xid) stays
    // sound after a restore. A cursor above every stored row would be invisible to that check (E1). No row exists between
    // the mark and the ceiling, so a capped cursor re-reads nothing.
    private static async Task<FeedPosition> HighWaterAsync(
        BlocwerkDbContext db, IFeedHorizon horizon, FeedPosition ceiling, FeedEpoch epoch, CancellationToken cancellationToken)
    {
        var newest = await horizon.InOrder(horizon.Between(db.WallChanges, FeedPosition.Start, ceiling))
            .Select(c => new { c.TxId, c.Seq })
            .LastOrDefaultAsync(cancellationToken);
        var stored = newest is null
            ? new FeedPosition(ceiling.TxId is null ? null : 0, 0) // SQLite positions carry no transaction id
            : new FeedPosition(newest.TxId, newest.Seq);
        return epoch.FloorSeq > 0 ? FeedPosition.Max(stored, new FeedPosition(epoch.FloorTxId, epoch.FloorSeq)) : stored;
    }

    // A cursor exactly at the floor saw the newest deleted row, so it lost nothing; one below it may have.
    private static bool IsBelowFloor(FeedPosition position, FeedEpoch epoch) =>
        epoch.FloorSeq > 0 && position < new FeedPosition(epoch.FloorTxId, epoch.FloorSeq);

    private async Task<BlocwerkDbContext> OpenReadContextAsync(CancellationToken cancellationToken)
    {
        var db = createContext();
        if (!db.Database.IsNpgsql())
        {
            return db;
        }

        try
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
            await db.Database.ExecuteSqlRawAsync(IdleTimeoutSql, cancellationToken);
            return db;
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    private async Task<FeedEpoch> EpochAsync(BlocwerkDbContext db, CancellationToken cancellationToken)
    {
        if (initialized && await FeedEpochStore.ReadAsync(db, cancellationToken) is { } epoch)
        {
            return epoch;
        }

        await InitializeAsync(cancellationToken);
        return await FeedEpochStore.ReadAsync(db, cancellationToken)
            ?? throw new InvalidOperationException("The feed epoch was not created.");
    }
}
