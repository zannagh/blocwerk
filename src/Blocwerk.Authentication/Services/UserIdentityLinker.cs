using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Authentication.Services;

/// <summary>
/// The single idempotent, race-safe seam for attaching a <see cref="UserIdentity"/> to a user. Both
/// the login/back-fill path and the account-link path funnel through here so a duplicate insert on the
/// unique (Provider, ProviderUserId) index can never become an unhandled <see cref="DbUpdateException"/>
/// (Postgres 23505) and surface as an HTTP 500.
/// </summary>
internal static class UserIdentityLinker
{
    /// <summary>
    /// Ensures a (<paramref name="provider"/>, <paramref name="providerUserId"/>) identity is attached to
    /// <paramref name="userId"/>. A pre-check resolves the common case without a write; a concurrent insert
    /// that beats the pre-check is caught (unique-violation only) and re-resolved so the caller still gets a
    /// clean answer instead of an exception.
    /// </summary>
    internal static async Task<IdentityLinkResult> EnsureLinkedAsync(
        BlocwerkDbContext db,
        Guid userId,
        string provider,
        string providerUserId)
    {
        var existing = await FindAsync(db, provider, providerUserId);
        if (existing is not null)
        {
            return existing.UserId == userId
                ? IdentityLinkResult.AlreadyLinkedToUser
                : IdentityLinkResult.LinkedToDifferentUser;
        }

        var entity = new UserIdentity
        {
            UserId = userId,
            Provider = provider,
            ProviderUserId = providerUserId,
        };
        db.UserIdentities.Add(entity);

        try
        {
            await db.SaveChangesAsync();
            return IdentityLinkResult.Linked;
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex))
        {
            // A concurrent request won the unique index between our pre-check and this save. Detach the
            // insert that failed so the (possibly shared) context stays usable — the caller may still
            // SaveChanges again — then re-read the winning row to decide ownership.
            db.Entry(entity).State = EntityState.Detached;
            var raced = await FindAsync(db, provider, providerUserId);
            return raced?.UserId == userId
                ? IdentityLinkResult.AlreadyLinkedToUser
                : IdentityLinkResult.LinkedToDifferentUser;
        }
    }

    private static Task<UserIdentity?> FindAsync(BlocwerkDbContext db, string provider, string providerUserId) =>
        db.UserIdentities
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Provider == provider && i.ProviderUserId == providerUserId);
}
