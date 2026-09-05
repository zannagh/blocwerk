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
public sealed partial class TopLoggerImportService : ITopLoggerImportService
{
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
            connected,
            connection.NeedsReauth,
            connection.LastSyncAt,
            connection.LastSyncAttemptedAt,
            connection.LastSyncOutcome,
            connection.LastError,
            ascentCount,
            unmapped);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TopLoggerUnmappedGrade>> GetUnmappedGradesAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        await using BlocwerkDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        // Group by the raw grade, collapsing null/empty into a single "" bucket so it can still be
        // resolved. Min(ClimbName) yields a deterministic sample without a per-group First() subquery.
        List<TopLoggerUnmappedGrade> grades = await db.ExternalAscents
            .AsNoTracking()
            .Where(a => a.UserId == userId && a.Source == ExternalSource.TopLogger && a.NeedsGradeMapping)
            .GroupBy(a => a.RawGrade ?? string.Empty)
            .Select(g => new TopLoggerUnmappedGrade(g.Key, g.Count(), g.Min(a => a.ClimbName)))
            .OrderByDescending(g => g.Count)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return grades;
    }

    /// <inheritdoc />
    public async Task<int> ResolveGradeMappingAsync(
        Guid userId, string rawGradeKey, string fontGrade, CancellationToken cancellationToken = default)
    {
        // Only a grade the scoring path recognises may be stored; otherwise the ascents would be marked
        // "mapped" yet silently score zero. Accepts a V-scale value from the picker and normalises it.
        string? normalized = NormalizeFontGrade(fontGrade);
        if (normalized is null)
        {
            return 0;
        }

        rawGradeKey ??= string.Empty;

        await using BlocwerkDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        db.CurrentUserId = userId;

        // Upsert the (user, raw grade) resolution so future syncs auto-apply it too.
        UserGradeMapping? mapping = await db.UserGradeMappings
            .FirstOrDefaultAsync(m => m.UserId == userId && m.RawGradeKey == rawGradeKey, cancellationToken)
            .ConfigureAwait(false);
        if (mapping is null)
        {
            db.UserGradeMappings.Add(new UserGradeMapping
            {
                UserId = userId,
                RawGradeKey = rawGradeKey,
                FontGrade = normalized,
            });
        }
        else
        {
            mapping.FontGrade = normalized;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Retroactively resolve the existing unmapped ascents in a single UPDATE. The empty bucket
        // matches both a null and an empty raw grade.
        bool emptyBucket = rawGradeKey.Length == 0;
        IQueryable<ExternalAscent> query = db.ExternalAscents
            .Where(a => a.UserId == userId && a.Source == ExternalSource.TopLogger && a.NeedsGradeMapping);
        query = emptyBucket
            ? query.Where(a => a.RawGrade == null || a.RawGrade == string.Empty)
            : query.Where(a => a.RawGrade == rawGradeKey);

        int updated = await query
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(a => a.MappedGrade, normalized)
                    .SetProperty(a => a.NeedsGradeMapping, false),
                cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "TopLogger grade resolution for user {UserId}: '{RawGrade}' → {FontGrade} on {Count} ascent(s).",
            userId, rawGradeKey, normalized, updated);
        return updated;
    }

    /// <summary>
    /// Normalises a picker value (Font or V scale) to a Font grade the scoring path knows, or null when
    /// blank or unrecognised.
    /// </summary>
    private static string? NormalizeFontGrade(string? grade)
    {
        if (string.IsNullOrWhiteSpace(grade))
        {
            return null;
        }

        string trimmed = grade.Trim();
        string? font = trimmed.StartsWith("V", StringComparison.OrdinalIgnoreCase)
            ? GradeScale.ToFont(trimmed)
            : trimmed;

        if (string.IsNullOrEmpty(font))
        {
            return null;
        }

        return GradeScoring.AllScores.ContainsKey(font) ? font : null;
    }

}
