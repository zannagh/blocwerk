// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Authentication.Apple;

/// <summary>
/// The account a web Sign in with Apple opens: the one linked to the Apple <c>sub</c>, or, for a <c>sub</c> nobody
/// has linked, a new account exactly as a first GitHub, Google or Microsoft sign-in makes one.
/// </summary>
/// <remarks>
/// The match is by the linked <c>sub</c> and nothing else. The e-mail Apple shares (often a private relay address) is
/// neither stored nor compared, so an Apple ID never merges into an account that happens to have the same address.
/// </remarks>
public sealed class AppleWebAccounts(
    IDbContextFactory<BlocwerkDbContext> contexts,
    ILogger<AppleWebAccounts> logger,
    TimeProvider? clock = null)
{
    /// <summary>The display name of an account Apple sent no name for.</summary>
    public const string FallbackName = "Apple user";

    private TimeProvider Clock => clock ?? TimeProvider.System;

    /// <summary>
    /// The live account linked to <paramref name="subject"/>, a new account linked to it when none is, or null when the
    /// linked account is deleted (its link is about to go with it, and no second account may take the <c>sub</c>).
    /// <paramref name="name"/> (Apple's first-authorization name) becomes the new account's display name.
    /// </summary>
    public async Task<User?> FindOrCreateAsync(string subject, string? name, CancellationToken ct = default)
    {
        var linked = await FindLinkedAsync(subject, ct);
        if (linked.Found)
        {
            return linked.User;
        }

        await using var db = await contexts.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;
        var displayName = string.IsNullOrWhiteSpace(name) ? FallbackName : name.Trim();
        var user = new User
        {
            Identifier = $"{displayName.Replace("__", "_", StringComparison.Ordinal)}__{subject}",
            DisplayName = displayName,
            Role = IdentityRole.User,
        };
        db.Users.Add(user);
        db.AppleLinks.Add(new AppleLink { UserId = user.Id, Subject = subject, LinkedAt = Clock.GetUtcNow() });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A parallel first sign-in with the same Apple ID won the unique index; open the account it made.
            return (await FindLinkedAsync(subject, ct)).User;
        }

        logger.LogInformation("Sign in with Apple made account {UserId}", user.Id);
        return user;
    }

    /// <summary>True when <paramref name="subject"/> is the Apple ID linked to <paramref name="userId"/>.</summary>
    public async Task<bool> IsLinkedToAsync(Guid userId, string subject, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;
        return await db.AppleLinks.AnyAsync(l => l.UserId == userId && l.Subject == subject, ct);
    }

    private async Task<(bool Found, User? User)> FindLinkedAsync(string subject, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;
        var userId = await db.AppleLinks
            .Where(l => l.Subject == subject)
            .Select(l => (Guid?)l.UserId)
            .FirstOrDefaultAsync(ct);
        if (userId is null)
        {
            return (false, null);
        }

        var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || user.IsDeleted || GhostUser.Is(user.Id))
        {
            logger.LogWarning("Sign in with Apple refused: the linked account {UserId} is not live", userId);
            return (true, null);
        }

        return (true, user);
    }
}
