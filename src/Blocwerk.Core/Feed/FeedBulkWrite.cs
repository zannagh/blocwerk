// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Feed;

/// <summary>
/// The write side of the bulk sites (design §5.3, B2). <c>ExecuteUpdate</c> and <c>ExecuteDelete</c> bypass
/// <c>SaveChanges</c> and with it the <see cref="FeedInterceptor"/>, so a site that changes something viewers see runs its
/// write through here: inside a transaction (its own, or the caller's when one is open), the feed lookup, the write, and
/// then the rows appended with raw SQL on the same connection (<see cref="WallChangeInsert"/>), so rows and write commit or
/// roll back together. The wakes follow the commit, as a save's do.
/// <para>
/// The feed side never fails the write. The lookup and the append each run under a savepoint, so a failure cannot abort
/// the caller's Postgres transaction, and is logged; the write then stands without its rows, and viewers catch up on
/// their next reload. The feed side never takes the request's token either: once the write has run, a cancelled request
/// must not turn it into a rollback.
/// </para>
/// </summary>
public static class FeedBulkWrite
{
    /// <summary>Runs <paramref name="write"/> and appends the rows built from its result.</summary>
    /// <typeparam name="T">The write's result, e.g. the affected row count.</typeparam>
    public static Task<T> RunAsync<T>(
        BlocwerkDbContext db, string site, Func<Task<T>> write, Func<T, IEnumerable<WallChange>> rows, ILogger logger, CancellationToken ct) =>
        RunAsync(db, site, () => Task.FromResult(true), write, (_, result) => rows(result), logger, ct);

    /// <summary>
    /// Reads <paramref name="facts"/> (what the rows need to know, e.g. the walls of the rows about to be deleted), runs
    /// <paramref name="write"/>, and appends the rows built from both. A failed lookup writes no row.
    /// </summary>
    /// <typeparam name="TFacts">What the rows need to know, read before the write.</typeparam>
    /// <typeparam name="T">The write's result, e.g. the affected row count.</typeparam>
    public static Task<T> RunAsync<TFacts, T>(
        BlocwerkDbContext db,
        string site,
        Func<Task<TFacts>> facts,
        Func<Task<T>> write,
        Func<TFacts, T, IEnumerable<WallChange>> rows,
        ILogger logger,
        CancellationToken ct)
        where TFacts : notnull =>
        RunAsync(db, site, facts, (Func<TFacts?, Task<T>>)(_ => write()), rows, logger, ct);

    /// <summary>
    /// As the overload above, but <paramref name="write"/> is handed the facts (null when the lookup failed), so it can
    /// write exactly the rows the lookup read and locked, and the feed rows describe exactly what it changed.
    /// </summary>
    /// <typeparam name="TFacts">What the rows need to know, read before the write.</typeparam>
    /// <typeparam name="T">The write's result, e.g. the affected row count.</typeparam>
    public static async Task<T> RunAsync<TFacts, T>(
        BlocwerkDbContext db,
        string site,
        Func<Task<TFacts>> facts,
        Func<TFacts?, Task<T>> write,
        Func<TFacts, T, IEnumerable<WallChange>> rows,
        ILogger logger,
        CancellationToken ct)
        where TFacts : notnull
    {
        await using var own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        var known = await LookupAsync(db, site, facts, logger);
        var result = await write(known.Ok ? known.Value : default);
        if (known.Ok)
        {
            await AppendAsync(db, site, Build(site, () => rows(known.Value!, result), logger), logger);
        }

        if (own is not null)
        {
            await own.CommitAsync(CancellationToken.None);
        }

        return result;
    }

    /// <summary>
    /// Appends <paramref name="rows"/> inside the open transaction on <paramref name="db"/>, under a savepoint, and holds
    /// their wakes for its commit. Collapses a wall's rows to one resync past <see cref="FeedCollapse.MaxRowsPerWall"/>,
    /// and narrows them by the ambient <see cref="FeedAudienceScope"/>, as the interceptor does. Never throws.
    /// </summary>
    public static async Task AppendAsync(BlocwerkDbContext db, string site, IEnumerable<WallChange> rows, ILogger logger)
    {
        var list = FeedCollapse.Apply(rows.ToList(), scope: null, DateTimeOffset.UtcNow);
        if (list.Count == 0)
        {
            return;
        }

        FeedAudienceScope.Apply(list);
        try
        {
            await FeedLookupSavepoint.RunAsync(db, () => InsertAsync(db, list), CancellationToken.None);
            FeedInterceptor.Of(db)?.HoldOrWake(db, list);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Feed rows of bulk site {Site} could not be written; {Count} row(s) lost, viewers catch up on their next reload", site, list.Count);
        }
    }

    /// <summary>The actor of a bulk write: the context's user, or none for a background writer.</summary>
    public static Guid? Actor(BlocwerkDbContext db) => db.CurrentUserId == Guid.Empty ? null : db.CurrentUserId;

    private static async Task<(bool Ok, TFacts? Value)> LookupAsync<TFacts>(BlocwerkDbContext db, string site, Func<Task<TFacts>> facts, ILogger logger)
    {
        try
        {
            return (true, await FeedLookupSavepoint.RunAsync(db, facts, CancellationToken.None));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Feed lookup of bulk site {Site} failed; the write goes ahead without feed rows", site);
            return (false, default);
        }
    }

    private static IEnumerable<WallChange> Build(string site, Func<IEnumerable<WallChange>> rows, ILogger logger)
    {
        try
        {
            return rows().ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Building the feed rows of bulk site {Site} failed; none are written", site);
            return [];
        }
    }

    // The rows learn their transaction id (Postgres) for the wakes; their Seq stays unknown, which nothing after the
    // insert reads (the own-write observers evict unconditionally).
    private static async Task<bool> InsertAsync(BlocwerkDbContext db, IReadOnlyList<WallChange> rows)
    {
        foreach (var row in rows)
        {
            row.TxId = await WallChangeInsert.ExecuteAsync(db, row, CancellationToken.None);
        }

        return true;
    }
}
