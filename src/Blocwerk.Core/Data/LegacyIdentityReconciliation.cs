using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Data;

/// <summary>
/// One-time, idempotent self-heal for duplicate accounts created before the UserIdentities table
/// (migration 20260829225724). Such a duplicate is a "legacy" <see cref="Entities.User"/> that is
/// identified only by the subject suffix of its <c>Identifier</c> ("{name}__{sub}"); when a DIFFERENT
/// user later links or logs in with the same provider subject, a <c>UserIdentity</c> row is created for
/// that subject on the other account, leaving two accounts for the same person.
///
/// This step finds each legacy row whose subject (<see cref="Entities.User.UserAuthId"/>) EXACTLY
/// matches a <c>UserIdentity.ProviderUserId</c> owned by another user and absorbs the legacy row INTO
/// the identity owner (the account the user actually signs in as keeps existing; the legacy row is
/// deleted). It runs on every start; once merged there is nothing left to match, so it no-ops.
///
/// Strict guards — a merge fires only when: (i) the subject match is EXACT; (ii) the subject maps to
/// exactly ONE distinct owning user; and (iii) that owner is not the legacy row itself. Anything
/// ambiguous is left untouched.
/// </summary>
public static class LegacyIdentityReconciliation
{
    public static async Task RunIfNeededAsync(
        IDbContextFactory<BlocwerkDbContext> factory,
        IAccountMergeService mergeService,
        ILogger logger)
    {
        var plan = await BuildPlanAsync(factory);
        if (plan.Count == 0)
        {
            return;
        }

        int merged = 0;
        foreach (var (legacyUserId, legacyName, ownerUserId, ownerName, subject) in plan)
        {
            try
            {
                logger.LogInformation(
                    "Reconciling legacy account {LegacyId} ({LegacyName}) into identity owner {OwnerId} ({OwnerName}) on subject {Subject}.",
                    legacyUserId,
                    legacyName,
                    ownerUserId,
                    ownerName,
                    subject);
                await mergeService.MergeUsersAsync(legacyUserId, ownerUserId);
                merged++;
            }
            catch (Exception ex)
            {
                // Isolate a single bad merge (e.g. a row already removed by a prior merge in this run)
                // so the rest of the plan still runs.
                logger.LogError(
                    ex,
                    "Legacy-identity reconciliation failed to merge {LegacyId} into {OwnerId}; skipping.",
                    legacyUserId,
                    ownerUserId);
            }
        }

        logger.LogInformation("Legacy-identity reconciliation merged {Count} duplicate account(s).", merged);
    }

    // Builds the merge plan from a single snapshot so the query logic stays separate from the mutating
    // loop. Each entry is an unambiguous (legacy → identity-owner) match.
    private static async Task<List<(Guid LegacyUserId, string LegacyName, Guid OwnerUserId, string OwnerName, string Subject)>>
        BuildPlanAsync(IDbContextFactory<BlocwerkDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();

        var identities = await db.UserIdentities
            .AsNoTracking()
            .Select(i => new { i.ProviderUserId, i.UserId })
            .ToListAsync();

        var plan = new List<(Guid, string, Guid, string, string)>();
        if (identities.Count == 0)
        {
            return plan;
        }

        // Map each provider subject to the DISTINCT set of users that own an identity for it.
        var ownersBySubject = identities
            .GroupBy(i => i.ProviderUserId)
            .ToDictionary(g => g.Key, g => g.Select(i => i.UserId).Distinct().ToList());

        var users = await db.Users
            .AsNoTracking()
            .Select(u => new { u.Id, u.Identifier, u.DisplayName })
            .ToListAsync();
        var displayNames = users.ToDictionary(u => u.Id, u => u.DisplayName);

        foreach (var user in users)
        {
            // The subject the legacy Identifier encodes ("{name}__{sub}"): the segment after the last "__".
            var subject = user.Identifier.Split("__").LastOrDefault();
            if (string.IsNullOrEmpty(subject) || !ownersBySubject.TryGetValue(subject, out var owners))
            {
                continue;
            }

            // Guard (ii): the subject must map to exactly ONE distinct owner. Guard (iii): and it must
            // not be this legacy row (that is just its own back-filled identity — nothing to merge).
            if (owners.Count != 1 || owners[0] == user.Id)
            {
                continue;
            }

            var ownerId = owners[0];
            plan.Add((
                user.Id,
                displayNames.GetValueOrDefault(user.Id, string.Empty),
                ownerId,
                displayNames.GetValueOrDefault(ownerId, string.Empty),
                subject));
        }

        return plan;
    }
}
