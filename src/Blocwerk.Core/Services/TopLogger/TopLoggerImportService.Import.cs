using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services.TopLogger;

/// <summary>
/// The import + attempt-bookkeeping half of <see cref="TopLoggerImportService"/>: turning fetched ticks
/// into deduped ascents, and stamping the connection's success/failure/reauth state on every path.
/// </summary>
public sealed partial class TopLoggerImportService
{
    private const int MaxErrorLength = 1024;

    /// <summary>
    /// Imports the given ticks (deduped) and stamps the attempt as a success, advancing the last-sync
    /// marker. A post-fetch import failure is recorded on a fresh context (the batch leaves this one
    /// poisoned) so it is never silently discarded. Shared by the full pull and the session reconcile.
    /// </summary>
    private async Task<TopLoggerSyncResult> ImportAndStampAsync(
        BlocwerkDbContext db,
        Guid userId,
        TopLoggerConnection connection,
        IReadOnlyList<TopLoggerTick> ticks,
        CancellationToken cancellationToken)
    {
        try
        {
            (int imported, int skipped, int unmapped) =
                await ImportTicksAsync(db, userId, ticks, cancellationToken).ConfigureAwait(false);

            connection.LastSyncAt = DateTimeOffset.UtcNow;
            connection.NeedsReauth = false;
            MarkAttempt(connection, TopLoggerSyncOutcome.Success, null);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            logger.LogInformation(
                "TopLogger sync for user {UserId}: {Imported} imported, {Skipped} skipped.", userId, imported, skipped);
            return TopLoggerSyncResult.Ok(imported, skipped, unmapped);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "TopLogger import for user {UserId} failed after fetch.", userId);
            await RecordImportErrorAsync(userId, ex, cancellationToken).ConfigureAwait(false);
            return TopLoggerSyncResult.Failed("Importing the fetched ascents failed. Please try again.");
        }
    }

    /// <summary>
    /// Records a fetch/probe failure on the connection: a throttle gets a calm retry-later message, any
    /// other error carries its (truncated) message. Shared by the full pull and the session reconcile.
    /// </summary>
    private async Task<TopLoggerSyncResult> HandleFetchFailureAsync(
        BlocwerkDbContext db,
        TopLoggerConnection connection,
        Guid userId,
        Exception ex,
        CancellationToken cancellationToken)
    {
        if (ex is TopLoggerThrottledException)
        {
            // Rate-limited (429) after backoff — not an auth problem. Record a clear, calm message and
            // stop; the user (or the next app-open) can retry later. Never hammer.
            logger.LogWarning(ex, "TopLogger rate-limited the sync for user {UserId}.", userId);
            MarkAttempt(
                connection,
                TopLoggerSyncOutcome.Failed,
                "TopLogger is rate-limiting us right now — please try again in a little while.");
        }
        else
        {
            logger.LogWarning(ex, "TopLogger sync for user {UserId} failed.", userId);
            MarkAttempt(
                connection, TopLoggerSyncOutcome.Failed, TopLoggerImportHelpers.Truncate(ex.Message, MaxErrorLength));
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return TopLoggerSyncResult.Failed(connection.LastError ?? "Sync failed.");
    }

    private async Task RecordImportErrorAsync(Guid userId, Exception ex, CancellationToken cancellationToken)
    {
        await using BlocwerkDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        TopLoggerConnection? connection = await db.TopLoggerConnections
            .FirstOrDefaultAsync(c => c.UserId == userId, cancellationToken)
            .ConfigureAwait(false);
        if (connection is not null)
        {
            MarkAttempt(
                connection, TopLoggerSyncOutcome.Failed, TopLoggerImportHelpers.Truncate(ex.Message, MaxErrorLength));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stamps the outcome of a sync attempt onto the connection: the attempt time (always now), the
    /// outcome, and the error (the failure reason, or null to clear it on success). Called on EVERY
    /// terminal path — success with data, success with nothing new, and every failure — so the profile
    /// card can always show when a sync was last attempted and whether it worked.
    /// </summary>
    private static void MarkAttempt(TopLoggerConnection connection, TopLoggerSyncOutcome outcome, string? error)
    {
        connection.LastSyncAttemptedAt = DateTimeOffset.UtcNow;
        connection.LastSyncOutcome = outcome;
        connection.LastError = error;
    }

    private async Task<TopLoggerSyncResult> FailReauthAsync(
        BlocwerkDbContext db,
        TopLoggerConnection connection,
        TopLoggerAuthException ex,
        CancellationToken cancellationToken)
    {
        // The token store's ClearAsync has already blanked the ciphertext for a rejected refresh token
        // in its own context. Here we only flag the tracked row: EF emits just the changed columns, so
        // the wiped token columns are never resurrected from this context's stale snapshot.
        logger.LogWarning(ex, "TopLogger session for user {UserId} needs reconnect.", connection.UserId);
        connection.NeedsReauth = true;
        MarkAttempt(connection, TopLoggerSyncOutcome.Failed, TopLoggerImportHelpers.Truncate(ex.Message, MaxErrorLength));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return TopLoggerSyncResult.ReauthRequired(connection.LastError ?? "Reconnect required.");
    }

    private static async Task<(int Imported, int Skipped, int Unmapped)> ImportTicksAsync(
        BlocwerkDbContext db, Guid userId, IReadOnlyList<TopLoggerTick> ticks, CancellationToken cancellationToken)
    {
        HashSet<string> existing = await TopLoggerImportHelpers.LoadExistingIdsAsync(db, userId, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<string, string> gradeMap =
            await TopLoggerImportHelpers.LoadGradeMapAsync(db, userId, cancellationToken).ConfigureAwait(false);
        Dictionary<string, ExternalGym> gymCache = new(StringComparer.Ordinal);
        Dictionary<Guid, GymCalibrationData?> calibrationCache = new();

        int imported = 0;
        int skipped = 0;
        int unmapped = 0;

        foreach (TopLoggerTick tick in ticks)
        {
            // A blank id can't be deduped, a null timestamp can't be clustered, and existing.Add is
            // false for anything already imported this run or in the database.
            if (string.IsNullOrWhiteSpace(tick.ExternalId) || tick.LoggedAt is null || !existing.Add(tick.ExternalId))
            {
                skipped++;
                continue;
            }

            ExternalGym? gym = await TopLoggerImportHelpers.GetOrCreateGymAsync(db, gymCache, tick, cancellationToken)
                .ConfigureAwait(false);
            GymCalibrationData? calibration =
                await TopLoggerImportHelpers.LoadCalibrationAsync(db, calibrationCache, gym, cancellationToken)
                    .ConfigureAwait(false);
            ExternalAscent ascent = TopLoggerImportHelpers.BuildAscent(userId, tick, gym, gradeMap, calibration);
            if (ascent.NeedsGradeMapping)
            {
                unmapped++;
            }

            // Cluster on the UTC instant: ActivityGrouping derives day/window bounds from this value and
            // writes them to timestamptz columns, which reject a non-zero (local) offset.
            Guid activityId = await ActivityGrouping
                .ResolveActivityIdAsync(db, userId, tick.LoggedAt.Value.ToUniversalTime(), wallId: null)
                .ConfigureAwait(false);
            ascent.ActivityId = activityId;
            TopLoggerImportHelpers.AttachGymToActivity(db, activityId, gym);

            db.ExternalAscents.Add(ascent);
            imported++;
        }

        return (imported, skipped, unmapped);
    }
}
