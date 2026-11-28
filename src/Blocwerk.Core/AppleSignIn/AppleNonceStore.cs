// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.DeviceLinks;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.AppleSignIn;

/// <summary>
/// The Sign in with Apple nonces: issued without a database write (<see cref="IAppleNonceCodec"/>), alive for
/// <see cref="Lifetime"/>, and used at most once, which is the only thing stored: a spent nonce's hash until it expires.
/// </summary>
/// <remarks>
/// Issuing costs nothing to store, so a flood of nonce requests cannot fill the database. A use inserts the hash; the
/// unique index lets exactly one of two concurrent uses in.
/// </remarks>
public sealed class AppleNonceStore(
    IDbContextFactory<BlocwerkDbContext> contexts,
    IAppleNonceCodec codec,
    TimeProvider? clock = null)
{
    /// <summary>How long a nonce may wait for its use.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private TimeProvider Clock => clock ?? TimeProvider.System;

    /// <summary>Issues a fresh nonce.</summary>
    public string Issue() => codec.Issue(Clock.GetUtcNow() + Lifetime);

    /// <summary>
    /// Spends the nonce: true for the one call that finds it made here, unexpired and not spent before. Not cancellable,
    /// so a dropped request still leaves the nonce spent.
    /// </summary>
    public async Task<bool> TryUseAsync(string nonce)
    {
        var now = Clock.GetUtcNow();
        if (!codec.TryRead(nonce, out var expiresAt) || expiresAt <= now)
        {
            return false;
        }

        await using var db = await contexts.CreateDbContextAsync(CancellationToken.None);
        db.CurrentUserId = Guid.Empty;
        await db.AppleSignInNonces.Where(n => n.ExpiresAt <= now).ExecuteDeleteAsync(CancellationToken.None);
        db.AppleSignInNonces.Add(new AppleSignInNonce { NonceHash = DeviceLinkPkce.Hash(nonce), ExpiresAt = expiresAt, UsedAt = now });
        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }
}
