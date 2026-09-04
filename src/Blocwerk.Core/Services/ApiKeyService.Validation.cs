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
        if (!ApiKeyTokens.LooksLikeApiKey(token))
        {
            return null;
        }

        var hash = ApiKeyTokens.Hash(token.Trim());

        // Validation runs before any user context exists, so the wall filter must not apply.
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;

        var key = await db.ApiKeys.IgnoreQueryFilters().FirstOrDefaultAsync(k => k.KeyHash == hash, ct);
        if (key is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (key.RevokedAt is not null || (key.ExpiresAt is not null && key.ExpiresAt <= now))
        {
            return null;
        }

        if (key.LastUsedAt is null || now - key.LastUsedAt.Value > LastUsedWriteInterval)
        {
            key.LastUsedAt = now;
            await db.SaveChangesAsync(ct);
        }

        return key;
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
