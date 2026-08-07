using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services.TopLogger;

/// <summary>
/// Connects a user's TopLogger account, stores the token encrypted, and imports ascents as
/// <see cref="ExternalAscent"/>s (deduped by source id, clustered into activities). The shared
/// <see cref="SyncConnectionAsync"/> is also used by the background sync (Phase 2).
/// </summary>
public sealed class TopLoggerService : ITopLoggerService
{
    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly ICurrentUserService currentUserService;
    private readonly ITopLoggerClient client;
    private readonly ITokenProtector tokenProtector;
    private readonly ILogger<TopLoggerService> logger;

    public TopLoggerService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ICurrentUserService currentUserService,
        ITopLoggerClient client,
        ITokenProtector tokenProtector,
        ILogger<TopLoggerService> logger)
    {
        this.dbContextFactory = dbContextFactory;
        this.currentUserService = currentUserService;
        this.client = client;
        this.tokenProtector = tokenProtector;
        this.logger = logger;
    }

    public async Task<TopLoggerStatus> GetStatusAsync()
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();

        var connection = await db.TopLoggerConnections.FirstOrDefaultAsync(c => c.UserId == user.Id);
        var count = connection is null
            ? 0
            : await db.ExternalAscents.CountAsync(a => a.UserId == user.Id && a.Source == ExternalSource.TopLogger);

        return new TopLoggerStatus(
            connection is not null,
            tokenProtector.IsConfigured,
            connection?.Email,
            connection?.LastSyncAt,
            connection?.LastError,
            count);
    }

    public async Task<TopLoggerSyncResult> ConnectAsync(string email, string password)
    {
        if (!tokenProtector.IsConfigured)
        {
            return new TopLoggerSyncResult(false, 0, "TopLogger sync is not enabled on this server (no encryption key configured).");
        }

        var user = await currentUserService.GetCurrentUserAsync();
        var auth = await client.SignInAsync(email, password);
        if (!auth.Success || string.IsNullOrEmpty(auth.Token))
        {
            return new TopLoggerSyncResult(false, 0, auth.Error ?? "TopLogger sign-in failed.");
        }

        await using var db = await dbContextFactory.CreateDbContextAsync();
        var connection = await db.TopLoggerConnections.FirstOrDefaultAsync(c => c.UserId == user.Id);
        if (connection is null)
        {
            connection = new TopLoggerConnection
            {
                UserId = user.Id,
                Email = email,
                TokenEncrypted = tokenProtector.Protect(auth.Token),
                UserUid = auth.UserUid,
                Backend = auth.Backend,
            };
            db.TopLoggerConnections.Add(connection);
        }
        else
        {
            connection.Email = email;
            connection.TokenEncrypted = tokenProtector.Protect(auth.Token);
            connection.UserUid = auth.UserUid;
            connection.Backend = auth.Backend;
            connection.LastError = null;
        }

        await db.SaveChangesAsync();

        try
        {
            var imported = await SyncConnectionAsync(db, connection, CancellationToken.None);
            return new TopLoggerSyncResult(true, imported, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TopLogger first sync failed after connect for {UserId}.", user.Id);
            connection.LastError = Sanitize(ex.Message);
            await db.SaveChangesAsync();
            return new TopLoggerSyncResult(true, 0, $"Connected, but the first import failed: {ex.Message}");
        }
    }

    public async Task<TopLoggerSyncResult> SyncNowAsync()
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();

        var connection = await db.TopLoggerConnections.FirstOrDefaultAsync(c => c.UserId == user.Id);
        if (connection is null)
        {
            return new TopLoggerSyncResult(false, 0, "Not connected to TopLogger.");
        }

        try
        {
            var imported = await SyncConnectionAsync(db, connection, CancellationToken.None);
            return new TopLoggerSyncResult(true, imported, null);
        }
        catch (TopLoggerAuthException)
        {
            connection.LastError = "reconnect needed";
            await db.SaveChangesAsync();
            return new TopLoggerSyncResult(false, 0, "Your TopLogger token expired — reconnect your account.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TopLogger sync failed for {UserId}.", user.Id);
            connection.LastError = Sanitize(ex.Message);
            await db.SaveChangesAsync();
            return new TopLoggerSyncResult(false, 0, ex.Message);
        }
    }

    public async Task DisconnectAsync(bool deleteImportedAscents)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();

        var connection = await db.TopLoggerConnections.FirstOrDefaultAsync(c => c.UserId == user.Id);
        if (connection is not null)
        {
            db.TopLoggerConnections.Remove(connection);
        }

        if (deleteImportedAscents)
        {
            var ascents = db.ExternalAscents.Where(a => a.UserId == user.Id && a.Source == ExternalSource.TopLogger);
            db.ExternalAscents.RemoveRange(ascents);
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Imports new ascents for a connection into <paramref name="db"/> (caller-provided so this can run
    /// inside connect or the background sync). Dedupes by external id and clusters each into an activity.
    /// </summary>
    public async Task<int> SyncConnectionAsync(BlocwerkDbContext db, TopLoggerConnection connection, CancellationToken cancellationToken)
    {
        var token = tokenProtector.Unprotect(connection.TokenEncrypted);
        var credentials = new TopLoggerCredentials(connection.Email, token, connection.UserUid, connection.Backend);

        // Full fetch + dedupe (robust against late-added historical logs); incremental is a Phase-2 refinement.
        var ascents = await client.GetAscentsAsync(credentials, since: null, cancellationToken);

        var existing = (await db.ExternalAscents
                .Where(a => a.UserId == connection.UserId && a.Source == ExternalSource.TopLogger)
                .Select(a => a.ExternalId)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var imported = 0;
        foreach (var dto in ascents)
        {
            if (!existing.Add(dto.ExternalId))
            {
                continue;
            }

            var ascent = new ExternalAscent
            {
                UserId = connection.UserId,
                Source = ExternalSource.TopLogger,
                ExternalId = dto.ExternalId,
                ClimbName = Truncate(dto.ClimbName, 256),
                GymName = Truncate(dto.GymName, 256),
                Grade = TopLoggerGradeMapper.ToFontGrade(dto.GradeRaw),
                Type = dto.Type,
                LoggedAt = dto.LoggedAt,
            };

            ascent.ActivityId = await ActivityGrouping.ResolveActivityIdAsync(db, connection.UserId, dto.LoggedAt, null);
            db.ExternalAscents.Add(ascent);
            imported++;
        }

        connection.LastSyncAt = DateTimeOffset.UtcNow;
        connection.LastError = null;
        await db.SaveChangesAsync(cancellationToken);
        return imported;
    }

    private static string? Truncate(string? value, int max) =>
        value is not null && value.Length > max ? value[..max] : value;

    private static string Sanitize(string message) =>
        message.Length > 1024 ? message[..1024] : message;
}
