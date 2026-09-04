using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <inheritdoc cref="IApiKeyService"/>
public partial class ApiKeyService : IApiKeyService
{
    /// <summary>
    /// A sensor may post once a second; stamping LastUsedAt on every call would turn every read
    /// into a write. One minute of staleness on a "last used" column is a fine trade.
    /// </summary>
    private static readonly TimeSpan LastUsedWriteInterval = TimeSpan.FromMinutes(1);

    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly ILogger<ApiKeyService> logger;

    public ApiKeyService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ILogger<ApiKeyService> logger)
    {
        this.dbContextFactory = dbContextFactory;
        this.logger = logger;
    }

    public async Task<(ApiKey Key, string Token)> CreateWallKeyAsync(
        Guid wallId,
        Guid actingUserId,
        string name,
        DateTimeOffset? expiresAt,
        CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;

        await WallAdminGuard.EnsureWallAdminAsync(db, wallId, actingUserId, ct);

        var key = await PersistAsync(db, ApiKeyScope.Wall, actingUserId, wallId, name, expiresAt, ct);
        logger.LogInformation("API key {ApiKeyId} issued for wall {WallId} by {UserId}", key.Key.Id, wallId, actingUserId);
        return key;
    }

    public async Task<(ApiKey Key, string Token)> CreateKioskKeyAsync(
        Guid wallId,
        Guid actingUserId,
        string name,
        DateTimeOffset? expiresAt,
        CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;

        await WallAdminGuard.EnsureWallAdminAsync(db, wallId, actingUserId, ct);

        var key = await PersistAsync(db, ApiKeyScope.Kiosk, actingUserId, wallId, name, expiresAt, ct);
        logger.LogInformation("Kiosk API key {ApiKeyId} issued for wall {WallId} by {UserId}", key.Key.Id, wallId, actingUserId);
        return key;
    }

    public async Task<(ApiKey Key, string Token)> CreateUserKeyAsync(
        Guid userId,
        Guid actingUserId,
        string name,
        DateTimeOffset? expiresAt,
        CancellationToken ct = default)
    {
        EnsureSelf(userId, actingUserId, "mint");

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;

        var key = await PersistAsync(db, ApiKeyScope.User, userId, null, name, expiresAt, ct);
        logger.LogInformation("API key {ApiKeyId} issued for user {UserId}", key.Key.Id, userId);
        return key;
    }

    public async Task<(ApiKey Key, string Token)> CreateInstallationKeyAsync(
        Guid actingUserId,
        string name,
        DateTimeOffset? expiresAt,
        CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;

        // There is no wall to hang this authority on, so it is the installation's own admin role
        // that gates it — read from the database here, not taken from the caller, for the same
        // reason WallAdminGuard is consulted above rather than trusted from the UI.
        await AppAdminGuard.EnsureAppAdminAsync(db, actingUserId, ct);

        var key = await PersistAsync(db, ApiKeyScope.Installation, actingUserId, null, name, expiresAt, ct);
        logger.LogInformation("Installation API key {ApiKeyId} issued by {UserId}", key.Key.Id, actingUserId);
        return key;
    }

    public async Task<IReadOnlyList<ApiKey>> GetWallKeysAsync(
        Guid wallId,
        Guid actingUserId,
        CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;

        await WallAdminGuard.EnsureWallAdminAsync(db, wallId, actingUserId, ct);

        return await db.ApiKeys
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(k => k.WallId == wallId)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ApiKey>> GetUserKeysAsync(
        Guid userId,
        Guid actingUserId,
        CancellationToken ct = default)
    {
        EnsureSelf(userId, actingUserId, "list");

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;

        return await db.ApiKeys
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(k => k.UserId == userId && k.Scope == ApiKeyScope.User)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ApiKey>> GetInstallationKeysAsync(
        Guid actingUserId,
        CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;

        await AppAdminGuard.EnsureAppAdminAsync(db, actingUserId, ct);

        return await db.ApiKeys
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(k => k.Scope == ApiKeyScope.Installation)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task RevokeAsync(Guid apiKeyId, Guid actingUserId, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;

        var key = await db.ApiKeys.IgnoreQueryFilters().FirstOrDefaultAsync(k => k.Id == apiKeyId, ct);
        if (key is null)
        {
            throw new InvalidOperationException("API key not found");
        }

        // Wall and kiosk keys both belong to a wall, so its admins govern them; an installation key
        // belongs to nobody in particular, so the installation's admins govern it — and its minter
        // alone must not be the one who can retire it; a user key is the creator's own business.
        bool allowed;
        if ((key.Scope is ApiKeyScope.Wall or ApiKeyScope.Kiosk) && key.WallId.HasValue)
        {
            allowed = await WallAdminGuard.IsWallAdminAsync(db, key.WallId.Value, actingUserId, ct);
        }
        else if (key.Scope == ApiKeyScope.Installation)
        {
            allowed = await AppAdminGuard.IsAppAdminAsync(db, actingUserId, ct);
        }
        else
        {
            allowed = key.UserId == actingUserId;
        }

        if (!allowed)
        {
            throw new UnauthorizedAccessException($"User {actingUserId} may not revoke API key {apiKeyId}.");
        }

        if (key.RevokedAt is null)
        {
            key.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("API key {ApiKeyId} revoked by {UserId}", apiKeyId, actingUserId);
        }
    }

    public async Task SetAnonymousKioskSettingAsync(
        Guid apiKeyId,
        Guid actingUserId,
        bool allowed,
        CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;

        var key = await db.ApiKeys.IgnoreQueryFilters().FirstOrDefaultAsync(k => k.Id == apiKeyId, ct);
        if (key is null)
        {
            throw new InvalidOperationException("API key not found");
        }

        if (key.Scope != ApiKeyScope.Kiosk || !key.WallId.HasValue)
        {
            throw new InvalidOperationException("Only a kiosk key has an anonymous setting flag.");
        }

        // Same authority as revoking the key: the wall's admins govern the wall's keys. Checked here
        // and not merely hidden in the panel, because the panel is an interactive component calling
        // straight into this service.
        if (!await WallAdminGuard.IsWallAdminAsync(db, key.WallId.Value, actingUserId, ct))
        {
            throw new UnauthorizedAccessException(
                $"User {actingUserId} may not change API key {apiKeyId}.");
        }

        if (key.AllowAnonymousKioskSetting != allowed)
        {
            key.AllowAnonymousKioskSetting = allowed;
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "API key {ApiKeyId} anonymous kiosk setting set to {State} by {UserId}",
                apiKeyId, allowed, actingUserId);
        }
    }

    /// <summary>
    /// A personal key is its owner's own business — there is no admin path onto someone else's,
    /// so the only legitimate acting user is the owner. Without this the caller-supplied
    /// <paramref name="userId"/> was the whole check, and any caller reaching the service could
    /// enumerate (or mint) another account's personal keys.
    /// </summary>
    private static void EnsureSelf(Guid userId, Guid actingUserId, string verb)
    {
        if (userId == Guid.Empty || actingUserId != userId)
        {
            throw new UnauthorizedAccessException($"User {actingUserId} may not {verb} API keys for user {userId}.");
        }
    }

    private static async Task<(ApiKey Key, string Token)> PersistAsync(
        BlocwerkDbContext db,
        ApiKeyScope scope,
        Guid userId,
        Guid? wallId,
        string name,
        DateTimeOffset? expiresAt,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("An API key needs a name.", nameof(name));
        }

        var (token, prefix) = ApiKeyTokens.Create();
        var key = new ApiKey
        {
            Name = name.Trim(),
            Scope = scope,
            UserId = userId,
            WallId = wallId,
            KeyHash = ApiKeyTokens.Hash(token),
            Prefix = prefix,
            ExpiresAt = expiresAt,
        };

        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(ct);
        return (key, token);
    }
}
