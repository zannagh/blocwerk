using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// The read side of <see cref="ApiKeyService"/>: turning a bearer token back into the key row it
/// names. Split from the minting half so neither file grows past what fits in one screenful of
/// review — the two halves have no state in common beyond the context factory.
/// </summary>
public partial class ApiKeyService
{
    public async Task<ApiKey?> ValidateAsync(string token, CancellationToken ct = default)
    {
        var key = await FindActiveAsync(token, ct);
        if (key is not null)
        {
            await MarkUsedAsync(key, ct);
        }

        return key;
    }

    public async Task<ApiKey?> FindActiveAsync(string token, CancellationToken ct = default)
    {
        if (!ApiKeyTokens.LooksLikeApiKey(token))
        {
            return null;
        }

        var hash = ApiKeyTokens.Hash(token.Trim());

        // Validation runs before any user context exists, so the wall filter must not apply.
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;

        var key = await db.ApiKeys.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(k => k.KeyHash == hash, ct);
        if (key is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (key.RevokedAt is not null || (key.ExpiresAt is not null && key.ExpiresAt <= now))
        {
            return null;
        }

        return key;
    }

    public async Task MarkUsedAsync(ApiKey key, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (key.LastUsedAt is not null && now - key.LastUsedAt.Value <= LastUsedWriteInterval)
        {
            return;
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;
        await db.ApiKeys.IgnoreQueryFilters()
            .Where(k => k.Id == key.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now), ct);
        key.LastUsedAt = now;
    }

    public async Task<Guid?> ValidateKioskAsync(string token, CancellationToken ct = default)
    {
        var key = await ValidateAsync(token, ct);
        if (key is null || key.Scope != ApiKeyScope.Kiosk)
        {
            return null;
        }

        return key.WallId;
    }
}
