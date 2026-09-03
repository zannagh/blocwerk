using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Authentication.Kiosk;

/// <summary>
/// Re-checks, against the database, that a kiosk registration is still good: the key exists, has not
/// been revoked, has not expired, is still <see cref="ApiKeyScope.Kiosk"/>, and still belongs to the
/// wall the cookie claims.
/// </summary>
/// <remarks>
/// The device cookie is unforgeable but not self-validating — it says what was true at registration
/// time. Re-checking on use is what makes "revoke the kiosk key" turn off every tablet using it,
/// rather than only the ones that happen to have lost their cookie. The check goes by key ID; the
/// token itself is never stored anywhere, on the device or here.
/// <para>
/// Results are cached for a few seconds per instance (i.e. per request or circuit) so a page render
/// that touches the context repeatedly does not hammer the database, and NEGATIVE results are never
/// cached — a revocation takes effect on the very next check.
/// </para>
/// </remarks>
public sealed class KioskKeyValidator : IKioskKeyValidator
{
    /// <summary>
    /// How long a positive result is trusted. Short enough that a revoked key stops working while
    /// the admin is still looking at the tablet.
    /// </summary>
    private static readonly TimeSpan PositiveCacheWindow = TimeSpan.FromSeconds(15);

    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly Dictionary<(Guid ApiKeyId, Guid WallId), DateTimeOffset> validUntil = [];

    public KioskKeyValidator(IDbContextFactory<BlocwerkDbContext> dbContextFactory)
    {
        this.dbContextFactory = dbContextFactory;
    }

    /// <summary>True while the kiosk key may still register this device to this wall.</summary>
    public async Task<bool> IsKeyValidAsync(Guid apiKeyId, Guid wallId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (validUntil.TryGetValue((apiKeyId, wallId), out var expires) && expires > now)
        {
            return true;
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);

        // The kiosk gate is stamped on this context, and ApiKeys are not filtered by it — but the
        // wall filter is disabled explicitly anyway, exactly as ApiKeyService.ValidateAsync does,
        // because this check runs before any user context exists.
        db.CurrentUserId = Guid.Empty;

        var valid = await db.ApiKeys
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(
                k => k.Id == apiKeyId
                     && k.WallId == wallId
                     && k.Scope == ApiKeyScope.Kiosk
                     && k.RevokedAt == null
                     && (k.ExpiresAt == null || k.ExpiresAt > now),
                ct);

        if (!valid)
        {
            validUntil.Remove((apiKeyId, wallId));
            return false;
        }

        validUntil[(apiKeyId, wallId)] = now.Add(PositiveCacheWindow);
        return true;
    }

    /// <summary>
    /// True while the user still consents to being picked at this wall's kiosk. Never cached:
    /// withdrawing consent must end a live session, which is a locked product decision.
    /// </summary>
    public async Task<bool> HasConsentAsync(Guid wallId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;

        return await db.WallMembers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(m => m.WallId == wallId && m.UserId == userId && m.KioskConsentedAt != null, ct);
    }
}
