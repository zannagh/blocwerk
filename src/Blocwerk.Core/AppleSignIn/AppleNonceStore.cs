// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.DeviceLinks;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.AppleSignIn;

/// <summary>
/// The nonces the server issues for Sign in with Apple: 256 random bits, stored as their SHA-256, alive for
/// <see cref="Lifetime"/> and used at most once.
/// </summary>
public sealed class AppleNonceStore(IDbContextFactory<BlocwerkDbContext> contexts, TimeProvider? clock = null)
{
    /// <summary>How long a nonce may wait for its use.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>How long a row is kept after it expired, then deleted when the next nonce is issued.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(1);

    private TimeProvider Clock => clock ?? TimeProvider.System;

    /// <summary>Issues a fresh nonce and stores its hash.</summary>
    public async Task<string> IssueAsync(CancellationToken ct = default)
    {
        var now = Clock.GetUtcNow();
        var nonce = DeviceLinkPkce.CreateCode();
        await using var db = await contexts.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;
        await db.AppleSignInNonces.Where(n => n.ExpiresAt < now - Retention).ExecuteDeleteAsync(ct);
        db.AppleSignInNonces.Add(new AppleSignInNonce
        {
            NonceHash = DeviceLinkPkce.Hash(nonce),
            CreatedAt = now,
            ExpiresAt = now + Lifetime,
        });
        await db.SaveChangesAsync(ct);
        return nonce;
    }

    /// <summary>
    /// Uses the nonce: true for the one call that finds it issued, unexpired and unused. The WHERE carries "unused and
    /// unexpired", so of two concurrent uses exactly one changes the row. Not cancellable, so a dropped request still
    /// leaves the nonce used.
    /// </summary>
    public async Task<bool> TryUseAsync(string nonce)
    {
        var now = Clock.GetUtcNow();
        var hash = DeviceLinkPkce.Hash(nonce);
        await using var db = await contexts.CreateDbContextAsync(CancellationToken.None);
        db.CurrentUserId = Guid.Empty;
        var won = await db.AppleSignInNonces
            .Where(n => n.NonceHash == hash && n.UsedAt == null && n.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.UsedAt, (DateTimeOffset?)now), CancellationToken.None);
        return won == 1;
    }
}
