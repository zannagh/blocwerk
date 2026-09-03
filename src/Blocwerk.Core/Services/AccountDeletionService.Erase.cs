using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// The erase half of <see cref="AccountDeletionService"/>: which rows go, which free text is wiped
/// off the rows that stay, and which columns are scrubbed off the tombstone. Everything here runs
/// inside the caller's transaction.
/// </summary>
public partial class AccountDeletionService
{
    /// <summary>
    /// Activity-log entries whose <see cref="ActivityLogEntry.Details"/> is text the USER typed
    /// rather than a sentence the app composed. Everything else in that column is machine-generated
    /// ("14 holds detected", "Role changed to Admin", "Send") and carries nothing about the person.
    /// </summary>
    /// <remarks>
    /// Add to this list whenever a new <c>LogAsync</c> call passes user-entered text as details, or
    /// that text will outlive the account that typed it.
    /// </remarks>
    private static readonly ActivityType[] UserAuthoredDetailTypes =
    [
        ActivityType.BoulderCreated,
        ActivityType.HoldNamed,
    ];

    /// <summary>
    /// Empties the tables that hold ONLY this person's own data — credentials, links, private
    /// training history, imports and memberships — and blanks the free text they typed on rows that
    /// stay. Tables holding content other members see (boulders, setter credits, comments, ratings,
    /// attempts, wall history, grade proposals, hold links, staging stamps) are deliberately kept:
    /// they keep pointing at the tombstone and render as
    /// <see cref="PlaceholderIdentity.DisplayName"/>.
    /// </summary>
    private static async Task ErasePersonalRowsAsync(
        BlocwerkDbContext db,
        Guid userId,
        AccountRefreshTokenOwnership tokenOwnership,
        CancellationToken ct)
    {
        // Credentials and login artefacts.
        await db.UserIdentities.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);
        await EraseRefreshTokensAsync(db, tokenOwnership, ct);

        // Scoped to the codes this ACCOUNT owns. Matching on the address instead would delete a
        // pending signup code somebody else had just requested for the same address — a code that
        // belongs to no account yet and is none of this deletion's business.
        await db.EmailVerificationCodes.Where(c => c.UserId == userId).ExecuteDeleteAsync(ct);

        await ReassignWallScopedApiKeysAsync(db, userId, ct);
        await db.ApiKeys.Where(k => k.UserId == userId).ExecuteDeleteAsync(ct);

        // Third-party import: the encrypted TopLogger tokens and everything pulled with them. This
        // is another service's data about the person; none of it belongs to a Blocwerk wall.
        await db.TopLoggerConnections.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);
        await db.ExternalAscents.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);
        await db.UserGradeMappings.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);

        // Private training history and the private lists. Nobody else's view depends on these, so
        // anonymising rather than deleting them would keep personal data for no reason.
        await db.HangboardSessions.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);
        await db.PullupSessions.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);
        await db.ClimbingSessions.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);
        await db.BoulderFavorites.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);

        // Activity clusters are a private view over the attempts, not content: the attempts survive
        // (Attempt.ActivityId is SetNull) so send counts on shared boulders do not change.
        await db.Activities.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);

        // Beta clips SHOW the person climbing, which makes them personal data however useful they
        // are to the wall. The files themselves are unlinked by the caller after the commit.
        await db.BetaVideos.Where(x => x.UploadedByUserId == userId).ExecuteDeleteAsync(ct);

        // Wall memberships, and with them the kiosk PIN hash and kiosk consent. The person has left
        // every wall; their contributed content stays on those walls without them.
        await db.WallMembers.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);

        await EraseFreeTextAsync(db, userId, ct);
    }

    /// <summary>
    /// Blanks the prose the person typed on rows that outlive them.
    /// </summary>
    /// <remarks>
    /// An anonymised row still leaks whoever wrote "hurt my shoulder on this one, Anna". The rows
    /// themselves stay — an attempt's send still counts towards its boulder, and a log entry still
    /// records that the wall was reset — but the free-text column on them does not.
    /// </remarks>
    private static async Task EraseFreeTextAsync(BlocwerkDbContext db, Guid userId, CancellationToken ct)
    {
        await db.Attempts
            .Where(a => a.UserId == userId && a.Notes != null)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Notes, (string?)null), ct);

        await db.ActivityLog
            .Where(a => a.UserId == userId && a.Details != null && UserAuthoredDetailTypes.Contains(a.Type))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Details, (string?)null), ct);
    }

    /// <summary>
    /// Deletes the refresh tokens that really are this account's, and only those. See
    /// <see cref="AccountRefreshTokenOwnership"/> for why the two sets are treated differently.
    /// </summary>
    private static async Task EraseRefreshTokensAsync(
        BlocwerkDbContext db,
        AccountRefreshTokenOwnership ownership,
        CancellationToken ct)
    {
        if (ownership.ExclusiveSubjects.Count > 0)
        {
            var exclusive = ownership.ExclusiveSubjects;
            await db.RefreshTokens
                .Where(t => exclusive.Contains(t.UserId))
                .ExecuteDeleteAsync(ct);
        }

        if (ownership.SharedSubjects.Count > 0 && ownership.KnownNames.Count > 0)
        {
            var shared = ownership.SharedSubjects;
            var names = ownership.KnownNames;
            await db.RefreshTokens
                .Where(t => shared.Contains(t.UserId) && names.Contains(t.UserName))
                .ExecuteDeleteAsync(ct);
        }
    }

    /// <summary>
    /// Works out which refresh-token subjects belong to this account alone, and which are shared with
    /// somebody else, before the identities that answer the question are deleted.
    /// </summary>
    private static async Task<AccountRefreshTokenOwnership> ResolveRefreshTokenOwnershipAsync(
        BlocwerkDbContext db,
        User user,
        CancellationToken ct)
    {
        var subjects = await db.UserIdentities
            .Where(i => i.UserId == user.Id)
            .Select(i => i.ProviderUserId)
            .ToListAsync(ct);
        subjects.Add(user.UserAuthId);

        subjects = subjects
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var shared = new HashSet<string>(StringComparer.Ordinal);

        var alsoLinkedElsewhere = await db.UserIdentities
            .Where(i => i.UserId != user.Id && subjects.Contains(i.ProviderUserId))
            .Select(i => i.ProviderUserId)
            .ToListAsync(ct);
        foreach (var subject in alsoLinkedElsewhere)
        {
            shared.Add(subject);
        }

        // A pre-UserIdentities account records its subject only as the tail of its identifier, so it
        // has to be matched the same way LegacyIdentityResolver does.
        foreach (var subject in subjects)
        {
            var suffix = "__" + subject;
            var candidates = await db.Users
                .Where(u => u.Id != user.Id && u.Identifier.EndsWith(suffix))
                .Select(u => u.Identifier)
                .ToListAsync(ct);

            if (candidates.Any(i => string.Equals(i.Split("__").LastOrDefault(), subject, StringComparison.Ordinal)))
            {
                shared.Add(subject);
            }
        }

        var knownNames = new[] { user.DisplayName, user.CustomDisplayName, user.LoginUsername, user.UserName }
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new AccountRefreshTokenOwnership
        {
            ExclusiveSubjects = subjects.Where(s => !shared.Contains(s)).ToList(),
            SharedSubjects = subjects.Where(shared.Contains).ToList(),
            KnownNames = knownNames,
        };
    }

    /// <summary>
    /// Hands wall- and kiosk-scoped API keys to the wall's (possibly just-transferred) owner before
    /// the user's keys are dropped, under a neutral name.
    /// </summary>
    /// <remarks>
    /// These keys authorise a wall, not a person: they are what a mounted tablet and a temperature
    /// sensor authenticate with. Deleting them because the admin who minted them left would silently
    /// unregister the gym's hardware. The NAME, though, is free text the departing admin chose ("Ida's
    /// spare tablet"), so it is replaced rather than handed over with the key. Personal
    /// (<see cref="ApiKeyScope.User"/>) keys act AS the user and are deleted with everything else.
    /// </remarks>
    private static async Task ReassignWallScopedApiKeysAsync(
        BlocwerkDbContext db,
        Guid userId,
        CancellationToken ct)
    {
        var wallKeys = await db.ApiKeys
            .Where(k => k.UserId == userId && k.WallId != null && k.Scope != ApiKeyScope.User)
            .Select(k => new { k.Id, k.Scope, k.Prefix, WallId = k.WallId!.Value })
            .ToListAsync(ct);

        if (wallKeys.Count == 0)
        {
            return;
        }

        var wallIds = wallKeys.Select(k => k.WallId).Distinct().ToList();
        var owners = await db.Walls
            .IgnoreQueryFilters()
            .Where(w => wallIds.Contains(w.Id))
            .Select(w => new { w.Id, w.OwnerId })
            .ToDictionaryAsync(w => w.Id, w => w.OwnerId, ct);

        foreach (var key in wallKeys)
        {
            if (!owners.TryGetValue(key.WallId, out var ownerId) || ownerId == userId)
            {
                continue;
            }

            var neutralName = $"{key.Scope} key {key.Prefix}";
            await db.ApiKeys
                .Where(k => k.Id == key.Id)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(k => k.UserId, ownerId)
                        .SetProperty(k => k.Name, neutralName),
                    ct);
        }
    }

    /// <summary>
    /// Scrubs every personal column off the user row and stamps it as a tombstone. What is left is a
    /// row with an opaque identifier, the placeholder name, a creation date and a deletion date.
    /// </summary>
    private static async Task ScrubUserRowAsync(BlocwerkDbContext db, User user, CancellationToken ct)
    {
        user.Identifier = PlaceholderIdentity.DeletedIdentifier(user.Id);
        user.DisplayName = PlaceholderIdentity.DisplayName;
        user.CustomDisplayName = null;

        user.AvatarImage = null;
        user.AvatarContentType = null;

        user.Email = null;
        user.EmailVerified = false;

        user.LoginUsername = null;
        user.PasswordHash = null;

        user.TotpSecretProtected = null;
        user.TotpEnabled = false;
        user.TotpLastUsedStep = null;

        user.FailedAuthCount = 0;
        user.LockoutUntil = null;

        // Drop any elevated role with the person, so a tombstone can never be an admin account.
        user.Role = IdentityRole.User;

        user.HomeWallId = null;
        user.DeletedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
    }
}
