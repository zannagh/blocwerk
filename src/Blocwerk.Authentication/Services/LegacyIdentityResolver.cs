using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Authentication.Services;

/// <summary>
/// The single, legacy-aware resolver that maps an OAuth provider identity (provider + subject) to the
/// local <see cref="User"/> that owns it. Shared by the login path (<see cref="CurrentUserService"/>)
/// and the account-link path (<c>AccountController</c>) so the two can never again diverge on which
/// account a provider identity belongs to — the divergence that let account linking silently fork a
/// user's history into a duplicate account.
///
/// Resolution is two-tier:
/// (a) a <see cref="UserIdentity"/> row for (provider, providerUserId); else
/// (b) a legacy <see cref="User"/> created before the UserIdentities table (migration
///     20260829225724), identified only by the subject suffix of its <see cref="User.Identifier"/>
///     ("{name}__{sub}") — i.e. <see cref="User.UserAuthId"/> == providerUserId.
///
/// Tier (b) is deliberately NOT provider-qualified: a legacy Identifier records no provider name. This
/// is accepted as low risk because provider subject-id spaces do not overlap in practice.
/// </summary>
public static class LegacyIdentityResolver
{
    /// <summary>
    /// Resolves the owning user via tier (a) then tier (b). Returns null when no account owns the
    /// identity yet. Entities are returned tracked on the supplied context.
    /// </summary>
    public static async Task<User?> FindByProviderIdentityAsync(BlocwerkDbContext db, string provider, string providerUserId)
    {
        if (string.IsNullOrEmpty(providerUserId))
        {
            return null;
        }

        // (a) A UserIdentity row is the authoritative mapping once it exists.
        if (!string.IsNullOrEmpty(provider))
        {
            var identity = await db.UserIdentities
                .Include(i => i.User)
                .FirstOrDefaultAsync(i => i.Provider == provider && i.ProviderUserId == providerUserId);
            if (identity is not null)
            {
                return identity.User;
            }
        }

        // (b) Fall back to a legacy row that only ever carried the subject in its Identifier.
        return await FindByLegacyIdentifierAsync(db, providerUserId);
    }

    /// <summary>
    /// Tier (b) only: finds a legacy user whose <see cref="User.UserAuthId"/> (the subject suffix of the
    /// "{name}__{sub}" identifier) EXACTLY equals <paramref name="providerUserId"/>. Returns null when
    /// providerUserId is empty or no legacy row matches. Exposed on its own so the account-link path can
    /// run this exact check after it has already inspected the UserIdentities table itself.
    /// </summary>
    public static async Task<User?> FindByLegacyIdentifierAsync(BlocwerkDbContext db, string providerUserId)
    {
        if (string.IsNullOrEmpty(providerUserId))
        {
            return null;
        }

        // Narrow to candidates in SQL by the "__{sub}" suffix, then confirm the EXACT UserAuthId match
        // in memory (UserAuthId is a computed property EF can't translate). EndsWith keeps the scan
        // cheap; the in-memory check guarantees the subject is the whole final "__"-segment rather than
        // a partial tail of a longer segment.
        var suffix = "__" + providerUserId;
        var candidates = await db.Users
            .Where(u => u.Identifier.EndsWith(suffix))
            .ToListAsync();

        return candidates.FirstOrDefault(u => u.UserAuthId == providerUserId);
    }
}
