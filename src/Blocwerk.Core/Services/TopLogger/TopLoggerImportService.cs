using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services.TopLogger;

/// <summary>
/// Default <see cref="ITopLoggerImportService"/>. Connecting is gated behind the user having a
/// password (a shared OAuth session must not attach a token store). Syncing pulls new ticks, upserts
/// them as deduped <see cref="ExternalAscent"/> rows, clusters each into an <see cref="Activity"/>
/// and maps grades. Uses <see cref="IDbContextFactory{TContext}"/> so it can run from a future
/// background worker; an auth failure is turned into a "needs reauth" result rather than thrown.
/// </summary>
public sealed class TopLoggerImportService : ITopLoggerImportService
{
    private const int MaxErrorLength = 1024;

    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly ITopLoggerApiClient apiClient;
    private readonly ITopLoggerTokenStore tokenStore;
    private readonly ILogger<TopLoggerImportService> logger;

    public TopLoggerImportService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ITopLoggerApiClient apiClient,
        ITopLoggerTokenStore tokenStore,
        ILogger<TopLoggerImportService> logger)
    {
        this.dbContextFactory = dbContextFactory;
        this.apiClient = apiClient;
        this.tokenStore = tokenStore;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<TopLoggerConnectResult> ConnectAsync(
        Guid userId, string accessToken, string refreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(refreshToken))
        {
            return TopLoggerConnectResult.Failed("Both an access token and a refresh token are required.");
        }

        await using (BlocwerkDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            User? user = await db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
                .ConfigureAwait(false);
            if (user is null)
            {
                return TopLoggerConnectResult.Failed("User not found.");
            }

            // Gate: only a password-protected account may attach a TopLogger token store.
            if (!user.HasPassword)
            {
                return TopLoggerConnectResult.NeedsPassword();
            }
        }

        // Expiries are unknown from the raw browser tokens; a null expiry is treated as usable and the
        // GraphQL client refreshes on the first UNAUTHENTICATED response.
        await tokenStore.SaveAsync(userId, new TopLoggerTokens(accessToken, null, refreshToken, null), cancellationToken)
            .ConfigureAwait(false);

        // Connecting only persists the tokens; the caller runs the initial sync as a separate,
        // visibly distinct phase (so a fast "connecting" step is told apart from the long data pull).
        return TopLoggerConnectResult.Connected(null);
    }

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

        // Re-sync only: skip the full climb-day + per-day-log pull when nothing is newer than the last
        // sync. A first sync (LastSyncAt == null) always does the full pull. This gate serves the manual
        // and any future background path alike, since both come through SyncAsync.
        if (connection.LastSyncAt is { } lastSync)
        {
            TopLoggerSyncResult? skip =
                await TrySkipUnchangedAsync(db, userId, connection, lastSync, cancellationToken).ConfigureAwait(false);
            if (skip is not null)
            {
                return skip;
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
            logger.LogWarning(ex, "TopLogger sync for user {UserId} failed.", userId);
            connection.LastError = TopLoggerImportHelpers.Truncate(ex.Message, MaxErrorLength);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return TopLoggerSyncResult.Failed(connection.LastError ?? "Sync failed.");
        }

        try
        {
            (int imported, int skipped, int unmapped) =
                await ImportTicksAsync(db, userId, ticks, cancellationToken).ConfigureAwait(false);

            connection.LastSyncAt = DateTimeOffset.UtcNow;
            connection.NeedsReauth = false;
            connection.LastError = null;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            logger.LogInformation(
                "TopLogger sync for user {UserId}: {Imported} imported, {Skipped} skipped.", userId, imported, skipped);
            return TopLoggerSyncResult.Ok(imported, skipped, unmapped);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The import batch leaves the context with pending (poisoned) changes, so record the error on
            // a fresh context rather than re-saving this one. Without this, a post-fetch failure would go
            // unrecorded and silently discard the entire pull.
            logger.LogError(ex, "TopLogger import for user {UserId} failed after fetch.", userId);
            await RecordImportErrorAsync(userId, ex, cancellationToken).ConfigureAwait(false);
            return TopLoggerSyncResult.Failed("Importing the fetched ascents failed. Please try again.");
        }
    }

    /// <summary>
    /// Cheap pre-check on a re-sync: returns a success-with-zero result to skip the full pull when no
    /// climb-day is newer than the last sync, or null to proceed. Auth failure is turned into a reauth
    /// result, matching the full pull's handling.
    /// </summary>
    private async Task<TopLoggerSyncResult?> TrySkipUnchangedAsync(
        BlocwerkDbContext db,
        Guid userId,
        TopLoggerConnection connection,
        DateTimeOffset lastSync,
        CancellationToken cancellationToken)
    {
        DateTimeOffset? latest;
        try
        {
            latest = await apiClient.GetLatestClimbDayAsync(userId, cancellationToken).ConfigureAwait(false);
        }
        catch (TopLoggerAuthException ex)
        {
            return await FailReauthAsync(db, connection, ex, cancellationToken).ConfigureAwait(false);
        }

        // Compare at day granularity: only skip when the newest climb-day predates the last sync's day.
        // An equal day still does the full pull, so ticks added later the same day are never missed
        // (the id-based dedupe makes that re-pull cheap). A null latest means no climb-days → nothing new.
        bool nothingNew = latest is null || latest.Value.UtcDateTime.Date < lastSync.UtcDateTime.Date;
        if (!nothingNew)
        {
            return null;
        }

        connection.LastSyncAt = DateTimeOffset.UtcNow;
        connection.NeedsReauth = false;
        connection.LastError = null;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "TopLogger sync for user {UserId} skipped: no new activity since {LastSync}.", userId, lastSync);
        return TopLoggerSyncResult.Ok(0, 0, 0);
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
            connection.LastError = TopLoggerImportHelpers.Truncate(ex.Message, MaxErrorLength);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(
        Guid userId, bool deleteImportedAscents, CancellationToken cancellationToken = default)
    {
        await using BlocwerkDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        db.CurrentUserId = userId;

        if (deleteImportedAscents)
        {
            await db.ExternalAscents
                .Where(a => a.UserId == userId && a.Source == ExternalSource.TopLogger)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        await db.TopLoggerConnections
            .Where(c => c.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TopLoggerStatus> GetStatusAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using BlocwerkDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        TopLoggerConnection? connection = await db.TopLoggerConnections
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.UserId == userId, cancellationToken)
            .ConfigureAwait(false);
        if (connection is null)
        {
            return TopLoggerStatus.Disconnected;
        }

        int ascentCount = await db.ExternalAscents
            .CountAsync(a => a.UserId == userId && a.Source == ExternalSource.TopLogger, cancellationToken)
            .ConfigureAwait(false);
        int unmapped = await db.ExternalAscents
            .CountAsync(
                a => a.UserId == userId && a.Source == ExternalSource.TopLogger && a.NeedsGradeMapping,
                cancellationToken)
            .ConfigureAwait(false);

        bool connected = !string.IsNullOrWhiteSpace(connection.RefreshTokenProtected) && !connection.NeedsReauth;
        return new TopLoggerStatus(
            connected, connection.NeedsReauth, connection.LastSyncAt, connection.LastError, ascentCount, unmapped);
    }

    private async Task<TopLoggerSyncResult> FailReauthAsync(
        BlocwerkDbContext db, TopLoggerConnection connection, TopLoggerAuthException ex, CancellationToken cancellationToken)
    {
        // The token store's ClearAsync has already blanked the ciphertext for a rejected refresh token
        // in its own context. Here we only flag the tracked row: EF emits just the changed columns, so
        // the wiped token columns are never resurrected from this context's stale snapshot.
        logger.LogWarning(ex, "TopLogger session for user {UserId} needs reconnect.", connection.UserId);
        connection.NeedsReauth = true;
        connection.LastError = TopLoggerImportHelpers.Truncate(ex.Message, MaxErrorLength);
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
            ExternalAscent ascent = TopLoggerImportHelpers.BuildAscent(userId, tick, gym, gradeMap);
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
