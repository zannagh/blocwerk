using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services.TopLogger;

/// <summary>
/// The sync-flow half of <see cref="TopLoggerImportService"/>: the full pull entry point and the
/// cheap re-sync pre-check that reconciles a session which grew after it was last imported, instead of
/// blindly skipping. The import + attempt-stamping it defers to live in the Import partial.
/// </summary>
public sealed partial class TopLoggerImportService
{
    /// <inheritdoc />
    public async Task<TopLoggerSyncResult> SyncAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using BlocwerkDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        db.CurrentUserId = userId;

        TopLoggerConnection? connection = await db.TopLoggerConnections
            .FirstOrDefaultAsync(c => c.UserId == userId, cancellationToken)
            .ConfigureAwait(false);
        if (connection is null)
        {
            return TopLoggerSyncResult.Failed("TopLogger is not connected.");
        }

        // Re-sync only: a cheap pre-check that skips or reconciles instead of always re-walking the whole
        // logbook. It may return a terminal result (skip / reconcile / reauth); null means "do the full
        // pull". A first sync (LastSyncAt == null) always does the full pull.
        if (connection.LastSyncAt is { } lastSync)
        {
            TopLoggerSyncResult? shortCircuit =
                await TryReconcileOrSkipAsync(db, userId, connection, lastSync, cancellationToken)
                    .ConfigureAwait(false);
            if (shortCircuit is not null)
            {
                return shortCircuit;
            }
        }

        IReadOnlyList<TopLoggerTick> ticks;
        try
        {
            ticks = await apiClient.GetTicksAsync(userId, connection.LastSyncAt, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TopLoggerAuthException ex)
        {
            return await FailReauthAsync(db, connection, ex, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await HandleFetchFailureAsync(db, connection, userId, ex, cancellationToken).ConfigureAwait(false);
        }

        return await ImportAndStampAsync(db, userId, connection, ticks, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Cheap re-sync pre-check. Probes only the newest session: a climb-day AFTER the last sync means
    /// genuinely new activity, so the full pull runs (returns null). Otherwise the newest session is on
    /// or before the last sync's day and may have GROWN since we imported it (e.g. a mid-session sync
    /// captured only the first ascents), so its ascents are reconciled — the delta is imported, deduped.
    /// With no sessions at all there is nothing to do, so it skips. Auth failure becomes a reauth result.
    /// </summary>
    private async Task<TopLoggerSyncResult?> TryReconcileOrSkipAsync(
        BlocwerkDbContext db,
        Guid userId,
        TopLoggerConnection connection,
        DateTimeOffset lastSync,
        CancellationToken cancellationToken)
    {
        TopLoggerSessionSummary? latest;
        try
        {
            latest = await apiClient.GetLatestSessionAsync(userId, cancellationToken).ConfigureAwait(false);
        }
        catch (TopLoggerAuthException ex)
        {
            return await FailReauthAsync(db, connection, ex, cancellationToken).ConfigureAwait(false);
        }

        if (latest is null)
        {
            return await SkipUnchangedAsync(db, connection, userId, lastSync, cancellationToken).ConfigureAwait(false);
        }

        // A session on a later calendar day than the last sync is genuinely new: defer to the full pull,
        // whose since-cutoff re-pull also re-imports any earlier same-window day that grew (deduped).
        if (latest.Date.UtcDateTime.Date > lastSync.UtcDateTime.Date)
        {
            return null;
        }

        return await ReconcileSessionAsync(db, userId, connection, latest, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reconciles a single session: pulls just that day's ticks and imports the delta (the import dedupes
    /// on the tick's external id, so ascents we already have are skipped, never double-imported). Counts
    /// as a successful sync whether or not it imported anything, advancing the last-sync marker.
    /// </summary>
    private async Task<TopLoggerSyncResult> ReconcileSessionAsync(
        BlocwerkDbContext db,
        Guid userId,
        TopLoggerConnection connection,
        TopLoggerSessionSummary session,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TopLoggerTick> ticks;
        try
        {
            ticks = await apiClient.GetSessionTicksAsync(userId, session.DateKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TopLoggerAuthException ex)
        {
            return await FailReauthAsync(db, connection, ex, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await HandleFetchFailureAsync(db, connection, userId, ex, cancellationToken).ConfigureAwait(false);
        }

        return await ImportAndStampAsync(db, userId, connection, ticks, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Skips the full pull as a successful no-op: stamps the attempt as a success and advances the
    /// last-sync marker, matching the full pull's success bookkeeping.
    /// </summary>
    private async Task<TopLoggerSyncResult> SkipUnchangedAsync(
        BlocwerkDbContext db,
        TopLoggerConnection connection,
        Guid userId,
        DateTimeOffset lastSync,
        CancellationToken cancellationToken)
    {
        connection.LastSyncAt = DateTimeOffset.UtcNow;
        connection.NeedsReauth = false;
        MarkAttempt(connection, TopLoggerSyncOutcome.Success, null);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "TopLogger sync for user {UserId} skipped: no new activity since {LastSync}.", userId, lastSync);
        return TopLoggerSyncResult.Ok(0, 0, 0);
    }
}
